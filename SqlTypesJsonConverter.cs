using Newtonsoft.Json;
using System;

namespace pod.xledger.sql_server;

public class SqlTypesJsonConverter : JsonConverter {
    public override bool CanConvert(Type objectType) {
        return objectType.Namespace == "Microsoft.SqlServer.Types";
    }

    public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer) {
        if (value == null) {
            writer.WriteNull();
            return;
        }

        if (value is Microsoft.SqlServer.Types.SqlHierarchyId hier) {
            if (hier.IsNull) { writer.WriteNull(); } else {
                writer.WriteValue(hier.ToString());
            }
        } else if (value is Microsoft.SqlServer.Types.SqlGeography geog) {
            if (geog.IsNull) {
                writer.WriteNull();
            } else {
                writer.WriteValue(geog.ToString());
            }
        } else if (value is Microsoft.SqlServer.Types.SqlGeometry geom) {
            if (geom.IsNull) {
                writer.WriteNull();
            } else {
                writer.WriteValue(geom.ToString());
            }
        } else {
            writer.WriteValue(value.ToString());
        }
    }

    public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer) {
        throw new NotImplementedException();
    }
}
