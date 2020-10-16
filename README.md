# pod.xledger.sql-server

[babashka](https://github.com/borkdude/babashka) [pod](https://github.com/babashka/babashka.pods) for SQL Server.

## Usage

```clojure
(require '[babashka.pods :as pods])

;; After compiling this solution with Visual Studio:
(pods/load-pod "C:/src/pod_sql_server/bin/Debug/netcoreapp3.1/pod.xledger.sql_server.exe")

(require '[pod.xledger.sql-server :as sql])

(sql/execute! {
   "connection-string" "Data Source=my.db.host;Application Name=my.script;Initial Catalog=my_db_name;Integrated Security=True" 
   "command-text" "select top 1 * from sys.objects"
   "multi-rs" true  ;; Return multiple result sets, or just the first?
   })

=> [[{:is_schema_published false, :object_id 3, :type_desc "SYSTEM_TABLE", :modify_date "2014-02-20T20:48:35.277", :name "sysrscols", :create_date "2014-02-20T20:48:35.27", :parent_object_id 0, :principal_id nil, :type "S ", :is_ms_shipped true, :is_published false, :schema_id 4}]]

;; When you expect only 1 row:

(sql/execute-one! {
   "connection-string" "Data Source=my.db.host;Application Name=my.script;Initial Catalog=my_db_name;Integrated Security=True"
   "command-text" "select top 1 * from sys.objects where object_id = @object_id"
   "parameters" {:object_id 3}})

=> {:is_schema_published false, :object_id 3, :type_desc "SYSTEM_TABLE", :modify_date "2014-02-20T20:48:35.277", :name "sysrscols", :create_date "2014-02-20T20:48:35.27", :parent_object_id 0, :principal_id nil, :type "S ", :is_ms_shipped true, :is_published false, :schema_id 4}

;; JSON output support (FOR JSON PATH)

(sql/execute! {
   "connection-string" "Data Source=my.db.host;Application Name=my.script;Initial Catalog=my_db_name;Integrated Security=True"
   "command-text" "
declare @People as table (
    id int primary key,
    [name] nvarchar(255),
    dad_id int    
)

insert into @People([id], [name], dad_id)
values (1, 'Bob', null),
       (2, 'John', 1),
       (3, 'Jack', 2),
       (4, 'Jill', 2);

select a.*,
    JSON_QUERY((select * from @People b where a.dad_id = b.id for json path, without_array_wrapper)) as dad,
    (select * from @People b where a.id = b.dad_id for json path) as children
from @People a
where id = 2 /* John */
for json path"})

=> [{:id 2, :name "John", :dad_id 1, 
     :dad {:id 1, :name "Bob"}, 
     :children [{:id 3, :name "Jack", :dad_id 2} {:id 4, :name "Jill", :dad_id 2}]}]
```

## Design issues:

### Why not copy the jdbc API more closely?

We prefer passing arguments by name instead of position, and supporting the positional argument approach would require rewriting the SQL before sending it the the database.

### Why do it as a pod?

Using SQL Server with Integration Authentication requires a native dependency, which is probably hard to get working with Graal.
