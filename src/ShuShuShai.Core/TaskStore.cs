using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace ShuShuShai.Core;

public sealed class TaskStore
{
    public string DataDirectory { get; }
    private readonly string connection;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    public TaskStore(string? directory = null)
    {
        DataDirectory = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ShuShuShai");
        Directory.CreateDirectory(DataDirectory);
        connection = new SqliteConnectionStringBuilder { DataSource = Path.Combine(DataDirectory, "tasks.db"), Pooling = false }.ToString();
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE IF NOT EXISTS tasks(id TEXT PRIMARY KEY,json TEXT NOT NULL,updated TEXT NOT NULL); CREATE TABLE IF NOT EXISTS exports(id TEXT PRIMARY KEY,taskId TEXT NOT NULL,json TEXT NOT NULL); CREATE TABLE IF NOT EXISTS settings(key TEXT PRIMARY KEY,value TEXT NOT NULL);";
        cmd.ExecuteNonQuery();
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS hidden_tasks(id TEXT PRIMARY KEY, deleted INTEGER NOT NULL)";
        cmd.ExecuteNonQuery();
    }
    private SqliteConnection Open() { var db = new SqliteConnection(connection); db.Open(); return db; }
    public void Save(PhotoTask task)
    {
        var json = JsonSerializer.Serialize(task, Json);
        using var db = Open(); using var tx = db.BeginTransaction(); using var cmd = db.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO tasks SELECT $id,$json,$updated WHERE NOT EXISTS
                (SELECT 1 FROM hidden_tasks WHERE id=$id AND deleted=1)
            ON CONFLICT(id) DO UPDATE SET json=$json,updated=$updated
            """;
        cmd.Parameters.AddWithValue("$id", task.Id);
        cmd.Parameters.AddWithValue("$json", json);
        cmd.Parameters.AddWithValue("$updated", task.LastUsed.ToString("O"));
        if (cmd.ExecuteNonQuery() == 0) throw new InvalidOperationException("此任务已在另一个窗口永久删除。");
        tx.Commit();
    }
    public List<PhotoTask> LoadAll(bool archived = false)
    {
        using var db = Open(); var list = new List<PhotoTask>(); bool migrate=false;
        using(var cmd = db.CreateCommand())
        {
            cmd.CommandText = """
                SELECT json FROM tasks
                WHERE COALESCE((SELECT hidden_tasks.deleted + 1 FROM hidden_tasks WHERE hidden_tasks.id=tasks.id), 0) = $archived
                ORDER BY updated DESC
                """;
            cmd.Parameters.AddWithValue("$archived", archived); using var reader=cmd.ExecuteReader();
            while(reader.Read())
            {
                string json=reader.GetString(0);using var document=JsonDocument.Parse(json);
                var task=JsonSerializer.Deserialize<PhotoTask>(json,Json)??throw new InvalidDataException("任务记录为空");
                if(!document.RootElement.TryGetProperty("SchemaVersion",out var version)||version.GetInt32()<3)
                {MigrateLegacyTask(task);migrate=true;}
                list.Add(task);
            }
        }
        if(migrate)
        {
            // BackupDatabase takes a consistent SQLite snapshot, including committed WAL data.
            string backups=Path.Combine(DataDirectory,"backups");Directory.CreateDirectory(backups);
            using(var backup=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.Combine(backups,$"before-v0.3-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.db"),Pooling=false}.ToString()))
            {backup.Open();db.BackupDatabase(backup);}
            using var tx=db.BeginTransaction();
            foreach(var task in list)
            {
                using var write=db.CreateCommand();write.Transaction=tx;write.CommandText="UPDATE tasks SET json=$json WHERE id=$id";
                write.Parameters.AddWithValue("$id",task.Id);write.Parameters.AddWithValue("$json",JsonSerializer.Serialize(task,Json));write.ExecuteNonQuery();
            }
            tx.Commit();
        }
        return list;
    }
    public void SetArchived(string id, bool archived)
    {
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = archived
            ? "INSERT OR IGNORE INTO hidden_tasks SELECT id,0 FROM tasks WHERE id=$id"
            : "DELETE FROM hidden_tasks WHERE id=$id AND deleted=0";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public void DeleteTask(string id)
    {
        using var db = Open(); using var tx = db.BeginTransaction(); using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.CommandText = "SELECT json FROM exports WHERE taskId=$id";
        using (var reader = cmd.ExecuteReader())
            while (reader.Read())
                if (JsonSerializer.Deserialize<ExportPlan>(reader.GetString(0), Json)?.Complete != true)
                    throw new InvalidOperationException("此任务有未完成的导出。请打开任务并处理导出后再永久删除。");
        cmd.CommandText = """
            INSERT INTO hidden_tasks VALUES($id,1) ON CONFLICT(id) DO UPDATE SET deleted=1;
            DELETE FROM tasks WHERE id=$id
            """;
        cmd.ExecuteNonQuery();
        tx.Commit();
    }
    public static void MigrateLegacyTask(PhotoTask task)
    {
        for(int i=0;i<task.Sources.Count;i++)
        {
            var source=task.Sources[i];if(source.Directories!=null)continue;
            var dirs=new HashSet<string>(PathRules.Comparer){PathRules.Normalize(source.Path)};
            if(source.Recursive)foreach(var f in task.Files.Where(f=>PathRules.IsWithin(f.Path,source.Path)))dirs.Add(Path.GetDirectoryName(f.Path)!);
            task.Sources[i]=source with{Recursive=false,Directories=dirs.OrderBy(d=>d,PathRules.Comparer).ToList()};
        }
        if(!string.IsNullOrEmpty(task.Filter.Folder))
        {task.Filter.SpecificSources=true;task.Filter.Directories=task.Sources.SelectMany(s=>s.Directories??[s.Path]).Where(d=>PathRules.IsWithin(d,task.Filter.Folder)).Distinct(PathRules.Comparer).ToList();task.Filter.Folder="";}
        var o=task.ExportOptions;
        o.NamingMode??=o.Linked?(o.Shared.Rename?ExportNamingMode.Linked:ExportNamingMode.Original):(o.Jpg.Rename||o.Raw.Rename?ExportNamingMode.Independent:ExportNamingMode.Original);
        task.Filter.IncludeUnrated=false;task.CompareLinkedRating=false;task.SchemaVersion=3;
    }
    public void SaveExport(ExportPlan plan)
    {
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO exports SELECT $id,$task,$json WHERE NOT EXISTS
                (SELECT 1 FROM hidden_tasks WHERE id=$task AND deleted=1)
            ON CONFLICT(id) DO UPDATE SET json=$json
            """;
        cmd.Parameters.AddWithValue("$id", plan.Id);
        cmd.Parameters.AddWithValue("$task", plan.TaskId);
        cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(plan, Json));
        if (cmd.ExecuteNonQuery() == 0) throw new InvalidOperationException("此任务已永久删除，无法保存导出计划。");
    }
    public List<ExportPlan> LoadExports(string? taskId = null)
    {
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = taskId == null ? "SELECT json FROM exports" : "SELECT json FROM exports WHERE taskId=$task";
        if (taskId != null) cmd.Parameters.AddWithValue("$task", taskId);
        using var reader = cmd.ExecuteReader(); var list = new List<ExportPlan>();
        while (reader.Read()) list.Add(JsonSerializer.Deserialize<ExportPlan>(reader.GetString(0), Json)!); return list;
    }
    public string? GetSetting(string key)
    {
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT value FROM settings WHERE key=$key"; cmd.Parameters.AddWithValue("$key", key); return cmd.ExecuteScalar() as string;
    }
    public void SetSetting(string key, string value)
    {
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "INSERT INTO settings VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=$value";
        cmd.Parameters.AddWithValue("$key", key); cmd.Parameters.AddWithValue("$value", value); cmd.ExecuteNonQuery();
    }
}
