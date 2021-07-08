using BencodeNET.Objects;
using BencodeNET.Parsing;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace pod.xledger.sql_server {
    public class PodHandler {
        Stream _inputStream;
        Stream _outputStream;
        PipeReader _reader;
        PipeWriter _writer;
        BencodeParser _parser;

        public PodHandler(Stream inputStream, Stream outputStream) {
            _inputStream = inputStream;
            _outputStream = outputStream;
            _reader = PipeReader.Create(inputStream);
            _writer = PipeWriter.Create(outputStream, new StreamPipeWriterOptions(leaveOpen: true));
            _parser = new BencodeParser();
        }

        public async Task HandleMessages() {
            var cts = new CancellationTokenSource();
            while (!cts.IsCancellationRequested && _inputStream.CanRead && _outputStream.CanWrite) {
                try {
                    var msg = await _parser.ParseAsync<BDictionary>(_reader, cts.Token);
                    if (msg.TryGetValue("op", out IBObject op)) {
                        var s = ((BString)op).ToString();
                        await HandleMessage(s, msg, cts);
                    }
                } catch (OperationCanceledException) {
                } catch (Exception ex) {
                    await SendException(null, ex.Message);
                }
            }
        }

        async Task HandleMessage(string operation, BDictionary msg, CancellationTokenSource cts) {
            switch (operation) {
                case "describe":
                    var resp = new BDictionary {
                        ["format"] = new BString("json"),
                        ["namespaces"] = new BList {
                            new BDictionary {
                                ["name"] = new BString("pod.xledger.sql-server"),
                                ["vars"] = new BList {
                                    new BDictionary { ["name"] = new BString("execute!") },
                                    new BDictionary { ["name"] = new BString("execute-one!") }
                                }
                             }
                        },
                        ["ops"] = new BDictionary {
                            ["shutdown"] = new BDictionary()
                        }
                    };
                    await resp.EncodeToAsync(_writer);
                    break;
                case "shutdown":
                    cts.Cancel();
                    break;
                case "invoke":
                    await HandleInvoke(msg, cts);
                    break;
                default:
                    break;
            }
        }

        public static string JSON(object o) {
            var s = JsonConvert.SerializeObject(o);
            return s;
        }

        public static JToken ParseJson(string s) {
            var reader = new JsonTextReader(new StringReader(s));

            // We don't need/want NewtonSoft to tamper with our data:
            reader.DateParseHandling = DateParseHandling.None;
            reader.DateTimeZoneHandling = DateTimeZoneHandling.RoundtripKind;

            return JToken.Load(reader);
        }

        public static class StatusMessages {
            public static readonly BList DONE_ERROR = new BList(new[] { "done", "error" });

            public static readonly BList DONE = new BList(new[] { "done" });
        }

        async Task SendException(string id, string exMessage, object exData = null) {
            var resp = new BDictionary {
                ["ex-message"] = new BString(exMessage),
                ["status"] = StatusMessages.DONE_ERROR
            };
            if (id != null) { resp["id"] = new BString(id); }
            if (exData != null) { resp["ex-data"] = new BString(JSON(exData)); }
            await resp.EncodeToAsync(_writer);
        }

        async Task SendResult(string id, object result, bool isJson = false) {
            var json = isJson ? (string)result : JSON(result);
            var resp = new BDictionary {
                ["id"] = new BString(id),
                ["value"] = new BString(json),
                ["status"] = StatusMessages.DONE
            };
            await resp.EncodeToAsync(_writer);
        }

        async Task HandleInvoke(BDictionary msg, CancellationTokenSource cts) {
            if (!(msg.TryGetNonBlankString("id", out var id)
                && msg.TryGetNonBlankString("var", out var varname))) {
                await SendException(id, "Missing \"id\" and/or \"var\" keys in \"invoke\" operation payload");
                return;
            }

            switch (varname) {
                case "pod.xledger.sql-server/execute!":
                    await HandleVar_Execute(id, msg);
                    break;
                case "pod.xledger.sql-server/execute-one!":
                    await HandleVar_ExecuteOne(id, msg);
                    break;
                case "pod.xledger.sql-server/execute-raw!":
                    await HandleVar_ExecuteRaw(id, msg);
                    break;
                default:
                    await SendException(id, $"No such var: \"{varname}\"");
                    break;
            }
        }

        async Task HandleVar_Execute_Internal(string id, BDictionary msg, bool expectOne = false, bool rawResults = false) {
            if (!msg.TryGetValue("args", out var beArgs) || !(beArgs is BString beArgsStr)) {
                await SendException(id, $"Missing required \"args\" argument.");
                return;
            }

            IReadOnlyDictionary<string, JToken> argMap;
            try {
                argMap = JsonConvert.DeserializeObject<IList<IReadOnlyDictionary<string, JToken>>>(beArgsStr.ToString()).First();
            } catch (Exception ex) {
                await SendException(id, $"Couldn't deserialize json payload. Expected a map. Error: {ex.Message}");
                return;
            }

            if (!argMap.TryGetNonBlankString("connection-string", out var connStr)) {
                await SendException(id, $"Missing required \"connection-string\" argument.");
                return;
            }

            if (!argMap.TryGetNonBlankString("command-text", out var commandText)) {
                await SendException(id, $"Missing required \"command-text\" argument.");
                return;
            }

            try {
                using (var conn = new SqlConnection(connStr))
                using (var cmd = conn.CreateCommand()) {
                    await conn.OpenAsync();
                    cmd.CommandText = commandText;

                    if (argMap.TryGetValue("command-type", out JToken commandTypeTok) 
                        && commandTypeTok.Type != JTokenType.Null) {
                        if (commandTypeTok.Type != JTokenType.String) {
                            await SendException(id, $"Expected string. Failing key: \"$.command-type\"");
                            return;
                        }
                        var commandType = commandTypeTok.Value<string>();
                        switch (commandType) {
                            case "stored-procedure":
                                cmd.CommandType = System.Data.CommandType.StoredProcedure;
                                break;
                            case "text":
                                break; // This is the default
                            default:
                                await SendException(id, $"Expected \"stored-procedure\" | \"text\". Failing key: \"$.command-type\"");
                                return;
                        }
                    }

                    if (argMap.TryGetValue("parameters", out JToken paramTok)
                        && paramTok is JObject paramObj) {
                        foreach (var item in paramObj) {
                            if (!(item.Value is JValue v)) {
                                await SendException(id, $"Can only accept simple values (integers, strings, etc) for parameters. Failing key: \"$.parameters.{item.Key}\"");
                                return;
                            }
                            cmd.Parameters.AddWithValue(item.Key, v.Value);
                        }
                    }

                    var results = new List<object>();

                    bool multiResultSet;
                    argMap.TryGetBool("multi-rs", out multiResultSet); // same key as next.jdbc

                    using (var rdr = await cmd.ExecuteReaderAsync()) {
                        do {
                            var fieldCount = rdr.FieldCount;
                            var rs = new ResultSet {
                                columns =
                                    Enumerable.Range(0, fieldCount)
                                    .Select(i => rdr.GetName(i))
                                    .ToArray()
                            };

                            var isJson = rs.columns.Length == 1 && rs.columns[0] == "JSON_F52E2B61-18A1-11d1-B105-00805F49916B";
                            if (isJson) {
                                var sb = new StringBuilder();
                                while (rdr.Read()) { sb.Append(rdr.GetString(0)); }
                                if (expectOne || !multiResultSet) {
                                    await SendResult(id, sb.ToString(), isJson: true);
                                    return;
                                } else {
                                    // @PERF - Think of a way to eliminate deserialize -> serialize for this case
                                    var obj = ParseJson(sb.ToString());
                                    results.Add(obj);
                                }
                            } else {
                                var rows = rs.rows = new List<object[]>();
                                while (rdr.Read()) {
                                    var row = new object[fieldCount];
                                    for (int i = 0; i < fieldCount; i++) {
                                        rdr.GetValues(row);
                                    }
                                    rows.Add(row);
                                }
                                if (expectOne) {
                                    if (rows.Count > 0) {
                                        await SendResult(id, ResultSet2DictArray(rs)[0]);
                                    } else {
                                        await SendResult(id, null);
                                    }
                                    return;
                                }
                                results.Add(rawResults ? (object)rs : ResultSet2DictArray(rs));
                            }
                        } while (rdr.NextResult() && multiResultSet);
                    }

                    object result = null;
                    if (rawResults || multiResultSet) {
                        result = results;
                    } else if (results.Count > 0) {
                        result = results[0];
                    }
                    await SendResult(id, result);
                }
            } catch (Exception ex) {
                await SendException(id, ex.Message);
            }
        }

        Dictionary<string, object>[] ResultSet2DictArray(ResultSet resultSet) {
            var ret = new Dictionary<string, object>[resultSet.rows.Count];
            for (int i = 0; i < resultSet.rows.Count; i++) {
                var d = ret[i] = new Dictionary<string, object>(resultSet.columns.Length);
                for (int j = 0; j < resultSet.columns.Length; j++) {
                    var k = resultSet.columns[j];
                    var v = resultSet.rows[i][j];
                    d[k] = v;
                }
            }
            return ret;
        }

        async Task HandleVar_Execute(string id, BDictionary msg) =>
            await HandleVar_Execute_Internal(id, msg);

        async Task HandleVar_ExecuteOne(string id, BDictionary msg) =>
            await HandleVar_Execute_Internal(id, msg, expectOne: true);

        async Task HandleVar_ExecuteRaw(string id, BDictionary msg) =>
            await HandleVar_Execute_Internal(id, msg, rawResults: true);
    }

    public class ResultSet {
        public string[] columns;
        public List<object[]> rows;
    }
}
