using BencodeNET.Objects;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace pod.xledger.sql_server {
    public static class Extensions {
        public static bool TryGetNonBlankString(this BDictionary d, string key, out string s) {
            if (!(d.TryGetValue(key, out IBObject op) && op is BString bs)) {
                s = null;
                return false;
            }

            var tmp = bs.ToString();
            if (!string.IsNullOrWhiteSpace(tmp)) {
                s = tmp;
                return true;
            } else {
                s = null;
                return false;
            }
        }

        public static bool TryGetNonBlankString(this IReadOnlyDictionary<string, object> d, string key, out string s) {
            if (!(d.TryGetValue(key, out var tmp) && tmp is string str)) {
                s = null;
                return false;
            }

            if (!string.IsNullOrWhiteSpace(str)) {
                s = str;
                return true;
            } else {
                s = null;
                return false;
            }
        }

        public static bool TryGetNonBlankString(this IReadOnlyDictionary<string, JToken> d, string key, out string s) {
            if (!(d.TryGetValue(key, out var tmp)
                && tmp.Type == JTokenType.String
                && null != (s = tmp.ToObject<string>()))) {
                s = null;
                return false;
            }

            if (!string.IsNullOrWhiteSpace(s)) {
                return true;
            } else {
                s = null;
                return false;
            }
        }

        public static bool TryGetBool(this IReadOnlyDictionary<string, JToken> d, string key, out bool b) {
            if (d.TryGetValue(key, out var tmp)
                && tmp.Type == JTokenType.Boolean
                && tmp is JValue tmp2) {
                b = (bool)tmp2.Value;
                return true;
            }
            b = false;
            return false;
        }
    }
}
