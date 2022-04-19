using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using SchemaZen.Library.Models.Comparers;

namespace SchemaZen.Library.Models;

public class Database {
	#region Compare

	public DatabaseDiff Compare(Database db) {
		var diff = new DatabaseDiff {
			Db = db
		};

		//compare database properties		   
		foreach (var p in from p in Props
		         let p2 = db.FindProp(p.Name)
		         where p.Script() != p2.Script()
		         select p)
			diff.PropsChanged.Add(p);

		//get tables added and changed
		foreach (var tables in new[] { Tables, TableTypes })
		foreach (var t in tables) {
			var t2 = db.FindTable(t.Name, t.Owner, t.IsType);
			if (t2 == null) {
				diff.TablesAdded.Add(t);
			} else {
				//compare mutual tables
				var tDiff = t.Compare(t2);
				if (!tDiff.IsDiff)
					continue;
				if (t.IsType) // types cannot be altered...
					diff.TableTypesDiff.Add(t);
				else
					diff.TablesDiff.Add(tDiff);
			}
		}

		//get deleted tables
		foreach (var t in db.Tables.Concat(db.TableTypes)
			         .Where(t => FindTable(t.Name, t.Owner, t.IsType) == null))
			diff.TablesDeleted.Add(t);

		//get procs added and changed
		foreach (var r in Routines) {
			var r2 = db.FindRoutine(r.Name, r.Owner);
			if (r2 == null) {
				diff.RoutinesAdded.Add(r);
			} else {
				//compare mutual procs
				if (r.Text.Trim() != r2.Text.Trim()) diff.RoutinesDiff.Add(r);
			}
		}

		//get procs deleted
		foreach (var r in db.Routines.Where(r => FindRoutine(r.Name, r.Owner) == null))
			diff.RoutinesDeleted.Add(r);

		//get added and compare mutual foreign keys
		foreach (var fk in ForeignKeys) {
			var fk2 = db.FindForeignKey(fk.Name, fk.Table.Owner);
			if (fk2 == null) {
				diff.ForeignKeysAdded.Add(fk);
			} else {
				if (fk.ScriptCreate() != fk2.ScriptCreate()) diff.ForeignKeysDiff.Add(fk);
			}
		}

		//get deleted foreign keys
		foreach (var fk in db.ForeignKeys
			         .Where(fk => FindForeignKey(fk.Name, fk.Table.Owner) == null))
			diff.ForeignKeysDeleted.Add(fk);

		//get added and compare mutual assemblies
		foreach (var a in Assemblies) {
			var a2 = db.FindAssembly(a.Name);
			if (a2 == null) {
				diff.AssembliesAdded.Add(a);
			} else {
				if (a.ScriptCreate() != a2.ScriptCreate()) diff.AssembliesDiff.Add(a);
			}
		}

		//get deleted assemblies
		foreach (var a in db.Assemblies.Where(a => FindAssembly(a.Name) == null))
			diff.AssembliesDeleted.Add(a);

		//get added and compare mutual users
		foreach (var u in Users) {
			var u2 = db.FindUser(u.Name);
			if (u2 == null) {
				diff.UsersAdded.Add(u);
			} else {
				if (u.ScriptCreate() != u2.ScriptCreate()) diff.UsersDiff.Add(u);
			}
		}

		//get deleted users
		foreach (var u in db.Users.Where(u => FindUser(u.Name) == null)) diff.UsersDeleted.Add(u);

		//get added and compare view indexes
		foreach (var c in ViewIndexes) {
			var c2 = db.FindViewIndex(c.Name);
			if (c2 == null) {
				diff.ViewIndexesAdded.Add(c);
			} else {
				if (c.ScriptCreate() != c2.ScriptCreate()) diff.ViewIndexesDiff.Add(c);
			}
		}

		//get deleted view indexes
		foreach (var c in db.ViewIndexes.Where(c => FindViewIndex(c.Name) == null))
			diff.ViewIndexesDeleted.Add(c);

		//get added and compare synonyms
		foreach (var s in Synonyms) {
			var s2 = db.FindSynonym(s.Name, s.Owner);
			if (s2 == null) {
				diff.SynonymsAdded.Add(s);
			} else {
				if (s.BaseObjectName != s2.BaseObjectName) diff.SynonymsDiff.Add(s);
			}
		}

		//get deleted synonyms
		foreach (var s in db.Synonyms.Where(s => FindSynonym(s.Name, s.Owner) == null))
			diff.SynonymsDeleted.Add(s);

		//get added and compare permissions
		foreach (var p in Permissions) {
			var p2 = db.FindPermission(p.Name);
			if (p2 == null) {
				diff.PermissionsAdded.Add(p);
			} else {
				if (p.ScriptCreate() != p2.ScriptCreate()) diff.PermissionsDiff.Add(p);
			}
		}

		//get deleted permissions
		foreach (var p in db.Permissions.Where(p => FindPermission(p.Name) == null))
			diff.PermissionsDeleted.Add(p);

		return diff;
	}

	#endregion

	#region " Constructors "

	private readonly Microsoft.Extensions.Logging.ILogger _logger;

	public Database(
		IList<string> filteredTypes = null,
		Microsoft.Extensions.Logging.ILogger logger = null
	) {
		// todo - make logger not nullable
		_logger = logger;
		ResetProps();


		filteredTypes = filteredTypes ?? new List<string>();
		foreach (var filteredType in filteredTypes) Dirs.Remove(filteredType);
	}

	public Database(
		string name,
		IList<string> filteredTypes = null,
		Microsoft.Extensions.Logging.ILogger logger = null
	)
		: this(filteredTypes, logger) {
		Name = name;
	}

	#endregion

	#region " Properties "

	public const string SqlWhitespaceOrCommentRegex = @"(?>(?:\s+|--.*?(?:\r|\n)|/\*.*?\*/))";
	public const string SqlEnclosedIdentifierRegex = @"\[.+?\]";
	public const string SqlQuotedIdentifierRegex = "\".+?\"";

	public const string SqlRegularIdentifierRegex = @"(?!\d)[\w@$#]+";
	// see rules for regular identifiers here https://msdn.microsoft.com/en-us/library/ms175874.aspx

	public List<SqlAssembly> Assemblies { get; set; } = new();
	public string Connection { get; set; } = "";
	public List<Table> DataTables { get; set; } = new();
	public string Dir { get; set; } = "";
	public List<ForeignKey> ForeignKeys { get; set; } = new();
	public string Name { get; set; }
	public int TimeoutSec { get; set; }

	public List<DbProp> Props { get; set; } = new();
	public List<Routine> Routines { get; set; } = new();
	public List<Schema> Schemas { get; set; } = new();
	public List<Synonym> Synonyms { get; set; } = new();
	public List<Table> TableTypes { get; set; } = new();
	public List<Table> Tables { get; set; } = new();
	public List<UserDefinedType> UserDefinedTypes { get; set; } = new();
	public List<Role> Roles { get; set; } = new();
	public List<SqlUser> Users { get; set; } = new();
	public List<Constraint> ViewIndexes { get; set; } = new();
	public List<Permission> Permissions { get; set; } = new();

	public DbProp FindProp(string name) {
		return Props.FirstOrDefault(
			p =>
				string.Equals(p.Name, name, StringComparison.CurrentCultureIgnoreCase));
	}

	public Table FindTable(string name, string owner, bool isTableType = false) {
		return FindTableBase(isTableType ? TableTypes : Tables, name, owner);
	}

	private static Table FindTableBase(IEnumerable<Table> tables, string name, string owner) {
		return tables.FirstOrDefault(t => t.Name == name && t.Owner == owner);
	}

	public Constraint FindConstraint(string name) {
		return Tables.SelectMany(t => t.Constraints).FirstOrDefault(c => c.Name == name);
	}

	public ForeignKey FindForeignKey(string name, string owner) {
		return ForeignKeys.FirstOrDefault(fk => fk.Name == name && fk.Table.Owner == owner);
	}

	public Routine FindRoutine(string name, string schema) {
		return Routines.FirstOrDefault(r => r.Name == name && r.Owner == schema);
	}

	public SqlAssembly FindAssembly(string name) {
		return Assemblies.FirstOrDefault(a => a.Name == name);
	}

	public SqlUser FindUser(string name) {
		return Users.FirstOrDefault(
			u =>
				string.Equals(u.Name, name, StringComparison.CurrentCultureIgnoreCase));
	}

	public Constraint FindViewIndex(string name) {
		return ViewIndexes.FirstOrDefault(c => c.Name == name);
	}

	public Synonym FindSynonym(string name, string schema) {
		return Synonyms.FirstOrDefault(s => s.Name == name && s.Owner == schema);
	}

	public Permission FindPermission(string name) {
		return Permissions.FirstOrDefault(g => g.Name == name);
	}

	public List<Table> FindTablesRegEx(string pattern, string excludePattern = null) {
		return Tables.Where(t => FindTablesRegExPredicate(t, pattern, excludePattern)).ToList();
	}

	private static bool FindTablesRegExPredicate(
		Table table,
		string pattern,
		string excludePattern) {
		var include = string.IsNullOrEmpty(pattern) || Regex.IsMatch(table.Name, pattern);
		var exclude = !string.IsNullOrEmpty(excludePattern) &&
			Regex.IsMatch(table.Name, excludePattern);

		return include && !exclude;
	}

	public static HashSet<string> Dirs { get; } = new() {
		"user_defined_types", "tables", "foreign_keys", "assemblies", "functions", "procedures",
		"triggers", "views", "xmlschemacollections", "data", "roles", "users", "synonyms",
		"table_types", "schemas", "props", "permissions", "check_constraints", "defaults"
	};

	public static string ValidTypes {
		get { return Dirs.Aggregate((x, y) => x + ", " + y); }
	}

	private void SetPropOnOff(string propName, object dbVal) {
		if (dbVal != DBNull.Value) FindProp(propName).Value = (bool)dbVal ? "ON" : "OFF";
	}

	private void SetPropString(string propName, object dbVal) {
		if (dbVal != DBNull.Value) FindProp(propName).Value = dbVal.ToString();
	}

	#endregion

	#region Load

	public void Load() {
		ResetProps();
		Schemas.Clear();
		Tables.Clear();
		UserDefinedTypes.Clear();
		TableTypes.Clear();
		ForeignKeys.Clear();
		Routines.Clear();
		DataTables.Clear();
		ViewIndexes.Clear();
		Assemblies.Clear();
		Users.Clear();
		Synonyms.Clear();
		Roles.Clear();
		Permissions.Clear();

		using (var cn = new SqlConnection(Connection)) {
			cn.Open();
			using (var cm = cn.CreateCommand()) {
				LoadProps(cm);
				LoadSchemas(cm);
				LoadTables(cm);
				LoadUserDefinedTypes(cm);
				LoadColumns(cm);
				LoadColumnIdentities(cm);
				LoadColumnDefaults(cm);
				LoadColumnComputes(cm);
				LoadConstraintsAndIndexes(cm);
				LoadCheckConstraints(cm);
				LoadForeignKeys(cm);
				LoadRoutines(cm);
				LoadXmlSchemas(cm);
				LoadCLRAssemblies(cm);
				LoadUsersAndLogins(cm);
				LoadSynonyms(cm);
				LoadRoles(cm);
				LoadPermissions(cm);
			}
		}
	}

	private void ResetProps() {
		Props.Clear();

		Props.Add(new DbProp("COMPATIBILITY_LEVEL", ""));
		Props.Add(new DbProp("COLLATE", ""));
		Props.Add(new DbProp("AUTO_CLOSE", ""));
		Props.Add(new DbProp("AUTO_SHRINK", ""));
		Props.Add(new DbProp("ALLOW_SNAPSHOT_ISOLATION", ""));
		Props.Add(new DbProp("READ_COMMITTED_SNAPSHOT", ""));
		Props.Add(new DbProp("RECOVERY", ""));
		Props.Add(new DbProp("PAGE_VERIFY", ""));
		Props.Add(new DbProp("AUTO_CREATE_STATISTICS", ""));
		Props.Add(new DbProp("AUTO_UPDATE_STATISTICS", ""));
		Props.Add(new DbProp("AUTO_UPDATE_STATISTICS_ASYNC", ""));
		Props.Add(new DbProp("ANSI_NULL_DEFAULT", ""));
		Props.Add(new DbProp("ANSI_NULLS", ""));
		Props.Add(new DbProp("ANSI_PADDING", ""));
		Props.Add(new DbProp("ANSI_WARNINGS", ""));
		Props.Add(new DbProp("ARITHABORT", ""));
		Props.Add(new DbProp("CONCAT_NULL_YIELDS_NULL", ""));
		Props.Add(new DbProp("NUMERIC_ROUNDABORT", ""));
		Props.Add(new DbProp("QUOTED_IDENTIFIER", ""));
		Props.Add(new DbProp("RECURSIVE_TRIGGERS", ""));
		Props.Add(new DbProp("CURSOR_CLOSE_ON_COMMIT", ""));
		Props.Add(new DbProp("CURSOR_DEFAULT", ""));
		Props.Add(new DbProp("TRUSTWORTHY", ""));
		Props.Add(new DbProp("DB_CHAINING", ""));
		Props.Add(new DbProp("PARAMETERIZATION", ""));
		Props.Add(new DbProp("DATE_CORRELATION_OPTIMIZATION", ""));
	}

	private void LoadSynonyms(SqlCommand cm) {
		cm.CommandText = @"
					select
						object_schema_name(object_id) as schema_name,
						name as synonym_name,
						base_object_name
					from sys.synonyms";
		using (var dr = cm.ExecuteReader()) {
			while (dr.Read()) {
				var synonym = new Synonym(
					(string)dr["synonym_name"],
					(string)dr["schema_name"]);
				synonym.BaseObjectName = (string)dr["base_object_name"];
				Synonyms.Add(synonym);
			}
		}
	}

	private void LoadPermissions(SqlCommand cm) {
		cm.CommandText = @"
					select
							u.name as user_name,
							object_schema_name(o.id) as object_owner,
							o.name as object_name,
							p.permission_name as permission
					from sys.database_permissions p
					join sys.sysusers u on p.grantee_principal_id = u.uid
					join sys.sysobjects o on p.major_id = o.id ";
		using (var dr = cm.ExecuteReader()) {
			while (dr.Read()) {
				var permission = new Permission(
					(string)dr["user_name"],
					(string)dr["object_owner"],
					(string)dr["object_name"],
					(string)dr["permission"]);
				Permissions.Add(permission);
			}
		}
	}

	private void LoadRoles(SqlCommand cm) {
		cm.CommandText = @"
select
	name
from sys.database_principals
where type = 'R'
	and name not in (
	-- Ignore default roles, just look for custom ones
		'db_accessadmin'
	,	'db_backupoperator'
	,	'db_datareader'
	,	'db_datawriter'
	,	'db_ddladmin'
	,	'db_denydatareader'
	,	'db_denydatawriter'
	,	'db_owner'
	,	'db_securityadmin'
	,	'public'
	)
";
		Role r = null;
		using (var dr = cm.ExecuteReader()) {
			while (dr.Read()) {
				r = new Role {
					Name = (string)dr["name"]
				};
				Roles.Add(r);
			}
		}
	}

	private void LoadUsersAndLogins(SqlCommand cm) {
		// get users that have access to the database
		cm.CommandText = @"
				select dp.name as UserName, USER_NAME(drm.role_principal_id) as AssociatedDBRole, default_schema_name
				from sys.database_principals dp
				left outer join sys.database_role_members drm on dp.principal_id = drm.member_principal_id
				where (dp.type_desc = 'SQL_USER' or dp.type_desc = 'WINDOWS_USER')
				and dp.sid not in (0x00, 0x01) and dp.name not in ('dbo', 'guest')
				and dp.is_fixed_role = 0
				order by dp.name";
		SqlUser u = null;
		using (var dr = cm.ExecuteReader()) {
			while (dr.Read()) {
				if (u == null || u.Name != (string)dr["UserName"])
					u = new SqlUser((string)dr["UserName"], (string)dr["default_schema_name"]);
				if (!(dr["AssociatedDBRole"] is DBNull))
					u.DatabaseRoles.Add((string)dr["AssociatedDBRole"]);
				if (!Users.Contains(u))
					Users.Add(u);
			}
		}

		try {
			// get sql logins
			cm.CommandText = @"
					select sp.name,  sl.password_hash
					from sys.server_principals sp
					inner join sys.sql_logins sl on sp.principal_id = sl.principal_id and sp.type_desc = 'SQL_LOGIN'
					where sp.name not like '##%##'
					and sp.name != 'SA'
					order by sp.name";
			using (var dr = cm.ExecuteReader()) {
				while (dr.Read()) {
					u = FindUser((string)dr["name"]);
					if (u != null && !(dr["password_hash"] is DBNull))
						u.PasswordHash = (byte[])dr["password_hash"];
				}
			}
		} catch (SqlException ex) {
			// todo detect this before query and get rid of try catch
			_logger?.LogWarning("assumed logins not supported by sql version, ignored ex:");
			_logger?.LogWarning(ex.Message);
		}
	}

	private void LoadCLRAssemblies(SqlCommand cm) {
		try {
			// get CLR assemblies
			cm.CommandText =
				@"select a.name as AssemblyName, a.permission_set_desc, af.name as FileName, af.content
						from sys.assemblies a
						inner join sys.assembly_files af on a.assembly_id = af.assembly_id 
						where a.is_user_defined = 1
						order by a.name, af.file_id";
			SqlAssembly a = null;
			using (var dr = cm.ExecuteReader()) {
				while (dr.Read()) {
					if (a == null || a.Name != (string)dr["AssemblyName"])
						a = new SqlAssembly(
							(string)dr["permission_set_desc"],
							(string)dr["AssemblyName"]);

					a.Files.Add(
						new KeyValuePair<string, byte[]>(
							(string)dr["FileName"],
							(byte[])dr["content"]));
					if (!Assemblies.Contains(a))
						Assemblies.Add(a);
				}
			}
		} catch (SqlException ex) {
			// todo detect this before query and get rid of try catch
			_logger?.LogWarning("assumed assemblies not supported by sql version, ignored ex:");
			_logger?.LogWarning(ex.Message);
		}
	}

	private void LoadXmlSchemas(SqlCommand cm) {
		try {
			// get xml schemas
			cm.CommandText = @"
						select s.name as DBSchemaName, x.name as XMLSchemaCollectionName, xml_schema_namespace(s.name, x.name) as definition
						from sys.xml_schema_collections x
						inner join sys.schemas s on s.schema_id = x.schema_id
						where s.name != 'sys'";
			using (var dr = cm.ExecuteReader()) {
				while (dr.Read()) {
					var r = new Routine(
						(string)dr["DBSchemaName"],
						(string)dr["XMLSchemaCollectionName"],
						this) {
						Text =
							string.Format(
								"CREATE XML SCHEMA COLLECTION {0}.{1} AS N'{2}'",
								dr["DBSchemaName"],
								dr["XMLSchemaCollectionName"],
								dr["definition"]),
						RoutineType = Routine.RoutineKind.XmlSchemaCollection
					};
					Routines.Add(r);
				}
			}
		} catch (SqlException ex) {
			// todo detect this before query and get rid of try catch
			_logger?.LogWarning("assumed xml schemas not supported by sql version, ignored ex:");
			_logger?.LogWarning(ex.Message);
		}
	}

	private void LoadRoutines(SqlCommand cm) {
		//get routines
		cm.CommandText = @"
					select
						s.name as schemaName,
						o.name as routineName,
						o.type_desc,
						m.definition,
						m.uses_ansi_nulls,
						m.uses_quoted_identifier,
						isnull(s2.name, s3.name) as tableSchema,
						isnull(t.name, v.name) as tableName,
						tr.is_disabled as trigger_disabled
					from sys.sql_modules m
						inner join sys.objects o on m.object_id = o.object_id
						inner join sys.schemas s on s.schema_id = o.schema_id
						left join sys.triggers tr on m.object_id = tr.object_id
						left join sys.tables t on tr.parent_id = t.object_id
						left join sys.views v on tr.parent_id = v.object_id
						left join sys.schemas s2 on s2.schema_id = t.schema_id
						left join sys.schemas s3 on s3.schema_id = v.schema_id
					where objectproperty(o.object_id, 'IsMSShipped') = 0
					";
		using (var dr = cm.ExecuteReader()) {
			while (dr.Read()) {
				var r = new Routine((string)dr["schemaName"], (string)dr["routineName"], this);
				r.Text = dr["definition"] is DBNull ? string.Empty : (string)dr["definition"];
				r.AnsiNull = (bool)dr["uses_ansi_nulls"];
				r.QuotedId = (bool)dr["uses_quoted_identifier"];
				Routines.Add(r);

				switch ((string)dr["type_desc"]) {
					case "SQL_STORED_PROCEDURE":
						r.RoutineType = Routine.RoutineKind.Procedure;
						break;
					case "SQL_TRIGGER":
						r.RoutineType = Routine.RoutineKind.Trigger;
						r.RelatedTableName = (string)dr["tableName"];
						r.RelatedTableSchema = (string)dr["tableSchema"];
						r.Disabled = (bool)dr["trigger_disabled"];
						break;
					case "SQL_SCALAR_FUNCTION":
					case "SQL_INLINE_TABLE_VALUED_FUNCTION":
					case "SQL_TABLE_VALUED_FUNCTION":
						r.RoutineType = Routine.RoutineKind.Function;
						break;
					case "VIEW":
						r.RoutineType = Routine.RoutineKind.View;
						break;
				}
			}
		}
	}

	private void LoadCheckConstraints(SqlCommand cm) {
		cm.CommandText = @"
				SELECT 
					OBJECT_NAME(o.OBJECT_ID) AS CONSTRAINT_NAME,
					SCHEMA_NAME(t.schema_id) AS TABLE_SCHEMA,
					OBJECT_NAME(o.parent_object_id) AS TABLE_NAME,
					CAST(0 AS bit) AS IS_TYPE,
					objectproperty(o.object_id, 'CnstIsNotRepl') AS NotForReplication,
					'CHECK' AS ConstraintType,
					cc.definition as CHECK_CLAUSE,
					cc.is_system_named
				FROM sys.objects o
					inner join sys.check_constraints cc on cc.object_id = o.object_id
					inner join sys.tables t on t.object_id = o.parent_object_id
					WHERE o.type_desc = 'CHECK_CONSTRAINT'
				UNION ALL
				SELECT 
					OBJECT_NAME(o.OBJECT_ID) AS CONSTRAINT_NAME,
					SCHEMA_NAME(tt.schema_id) AS TABLE_SCHEMA,
					tt.name AS TABLE_NAME,
					CAST(1 AS bit) AS IS_TYPE,
					objectproperty(o.object_id, 'CnstIsNotRepl') AS NotForReplication,
					'CHECK' AS ConstraintType,
					cc.definition as CHECK_CLAUSE,
					cc.is_system_named
				FROM sys.objects o
					inner join sys.check_constraints cc on cc.object_id = o.object_id
					inner join sys.table_types tt on tt.type_table_object_id = o.parent_object_id
					WHERE o.type_desc = 'CHECK_CONSTRAINT'
				ORDER BY TABLE_SCHEMA, TABLE_NAME, CONSTRAINT_NAME
				";

		using (var dr = cm.ExecuteReader()) {
			while (dr.Read()) {
				var t = FindTable(
					(string)dr["TABLE_NAME"],
					(string)dr["TABLE_SCHEMA"],
					(bool)dr["IS_TYPE"]);
				if (t != null) {
					var constraint = Constraint.CreateCheckedConstraint(
						(string)dr["CONSTRAINT_NAME"],
						Convert.ToBoolean(dr["NotForReplication"]),
						Convert.ToBoolean(dr["is_system_named"]),
						(string)dr["CHECK_CLAUSE"]
					);

					t.AddConstraint(constraint);
				}
			}
		}
	}

	private void LoadForeignKeys(SqlCommand cm) {
		//get foreign keys
		cm.CommandText = @"
					select 
						TABLE_SCHEMA,
						TABLE_NAME, 
						CONSTRAINT_NAME
					from INFORMATION_SCHEMA.TABLE_CONSTRAINTS
					where CONSTRAINT_TYPE = 'FOREIGN KEY'";
		using (var dr = cm.ExecuteReader()) {
			while (dr.Read()) {
				var t = FindTable((string)dr["TABLE_NAME"], (string)dr["TABLE_SCHEMA"]);
				var fk = new ForeignKey((string)dr["CONSTRAINT_NAME"]);
				fk.Table = t;
				ForeignKeys.Add(fk);
			}
		}

		//get foreign key props
		cm.CommandText = @"
					select 
						CONSTRAINT_NAME, 
						OBJECT_SCHEMA_NAME(fk.parent_object_id) as TABLE_SCHEMA,
						UPDATE_RULE, 
						DELETE_RULE,
						fk.is_disabled,
                        fk.is_system_named
					from INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS rc
						inner join sys.foreign_keys fk on rc.CONSTRAINT_NAME = fk.name and rc.CONSTRAINT_SCHEMA = OBJECT_SCHEMA_NAME(fk.parent_object_id)";
		using (var dr = cm.ExecuteReader()) {
			while (dr.Read()) {
				var fk = FindForeignKey(
					(string)dr["CONSTRAINT_NAME"],
					(string)dr["TABLE_SCHEMA"]);
				fk.OnUpdate = (string)dr["UPDATE_RULE"];
				fk.OnDelete = (string)dr["DELETE_RULE"];
				fk.Check = !(bool)dr["is_disabled"];
				fk.IsSystemNamed = (bool)dr["is_system_named"];
			}
		}

		//get foreign key columns and ref table
		cm.CommandText = @"
select
	fk.name as CONSTRAINT_NAME,
	OBJECT_SCHEMA_NAME(fk.parent_object_id) as TABLE_SCHEMA,
	c1.name as COLUMN_NAME,
	OBJECT_SCHEMA_NAME(fk.referenced_object_id) as REF_TABLE_SCHEMA,
	OBJECT_NAME(fk.referenced_object_id) as REF_TABLE_NAME,
	c2.name as REF_COLUMN_NAME
from sys.foreign_keys fk
inner join sys.foreign_key_columns fkc
	on fkc.constraint_object_id = fk.object_id
inner join sys.columns c1
	on fkc.parent_column_id = c1.column_id
	and fkc.parent_object_id = c1.object_id
inner join sys.columns c2
	on fkc.referenced_column_id = c2.column_id
	and fkc.referenced_object_id = c2.object_id
order by fk.name, fkc.constraint_column_id
";
		using (var dr = cm.ExecuteReader()) {
			while (dr.Read()) {
				var fk = FindForeignKey(
					(string)dr["CONSTRAINT_NAME"],
					(string)dr["TABLE_SCHEMA"]);
				if (fk == null) continue;

				fk.Columns.Add((string)dr["COLUMN_NAME"]);
				fk.RefColumns.Add((string)dr["REF_COLUMN_NAME"]);
				if (fk.RefTable == null)
					fk.RefTable = FindTable(
						(string)dr["REF_TABLE_NAME"],
						(string)dr["REF_TABLE_SCHEMA"]);
			}
		}
	}

	private void LoadConstraintsAndIndexes(SqlCommand cm) {
		//get constraints & indexes
		cm.CommandText = @"
					select 
						s.name as schemaName,
						t.name as tableName, 
						t.baseType,
						i.name as indexName, 
						c.name as columnName,
						i.is_primary_key, 
						i.is_unique_constraint,
						i.is_unique, 
						i.type_desc,
						i.filter_definition,
						isnull(ic.is_included_column, 0) as is_included_column,
						ic.is_descending_key,
						i.type
					from (
						select object_id, name, schema_id, 'T' as baseType
						from   sys.tables
						union
						select object_id, name, schema_id, 'V' as baseType
						from   sys.views
						union
						select type_table_object_id, name, schema_id, 'TVT' as baseType
						from   sys.table_types
						) t
						inner join sys.indexes i on i.object_id = t.object_id
						inner join sys.index_columns ic on ic.object_id = t.object_id
							and ic.index_id = i.index_id
						inner join sys.columns c on c.object_id = t.object_id
							and c.column_id = ic.column_id
						inner join sys.schemas s on s.schema_id = t.schema_id
					where i.type_desc != 'HEAP'
					order by s.name, t.name, i.name, ic.key_ordinal, ic.index_column_id";
		using (var dr = cm.ExecuteReader()) {
			while (dr.Read()) {
				var schemaName = (string)dr["schemaName"];
				var tableName = (string)dr["tableName"];
				var indexName = (string)dr["indexName"];
				var isView = (string)dr["baseType"] == "V";

				var t = isView
					? new Table(schemaName, tableName)
					: FindTable(tableName, schemaName, (string)dr["baseType"] == "TVT");
				var c = t.FindConstraint(indexName);

				if (c == null) {
					c = new Constraint(indexName, "", "");
					t.AddConstraint(c);
				}

				if (isView) {
					if (ViewIndexes.Any(v => v.Name == indexName))
						c = ViewIndexes.First(v => v.Name == indexName);
					else
						ViewIndexes.Add(c);
				}

				c.IndexType = dr["type_desc"] as string;
				c.Unique = (bool)dr["is_unique"];
				c.Filter = dr["filter_definition"] as string;
				if ((bool)dr["is_included_column"])
					c.IncludedColumns.Add((string)dr["columnName"]);
				else
					c.Columns.Add(
						new ConstraintColumn(
							(string)dr["columnName"],
							(bool)dr["is_descending_key"]));

				c.Type = "INDEX";
				if ((bool)dr["is_primary_key"])
					c.Type = "PRIMARY KEY";
				if ((bool)dr["is_unique_constraint"])
					c.Type = "UNIQUE";
			}
		}
	}

	private void LoadColumnComputes(SqlCommand cm) {
		//get computed column definitions
		cm.CommandText = @"
					select
						object_schema_name(t.object_id) as TABLE_SCHEMA,
						object_name(t.object_id) as TABLE_NAME,
						cc.name as COLUMN_NAME,
						cc.definition as DEFINITION,
						cc.is_persisted as PERSISTED,
						cc.is_nullable as NULLABLE,
						cast(0 as bit) as IS_TYPE
					from sys.computed_columns cc
					inner join sys.tables t on cc.object_id = t.object_id
					UNION ALL
					select 
						SCHEMA_NAME(tt.schema_id) as TABLE_SCHEMA,
						tt.name as TABLE_NAME,
						cc.name as COLUMN_NAME,
						cc.definition as DEFINITION,
						cc.is_persisted as PERSISTED,
						cc.is_nullable as NULLABLE,
						cast(1 as bit) AS IS_TYPE
					from sys.computed_columns cc
					inner join sys.table_types tt on cc.object_id = tt.type_table_object_id
					";
		using (var dr = cm.ExecuteReader()) {
			while (dr.Read()) {
				var t = FindTable(
					(string)dr["TABLE_NAME"],
					(string)dr["TABLE_SCHEMA"],
					(bool)dr["IS_TYPE"]);
				if (t != null) {
					var column = t.Columns.Find((string)dr["COLUMN_NAME"]);
					if (column != null) {
						column.ComputedDefinition = (string)dr["DEFINITION"];
						column.Persisted = (bool)dr["PERSISTED"];
					}
				}
			}
		}
	}

	private void LoadColumnDefaults(SqlCommand cm) {
		//get column defaults
		cm.CommandText = @"
					select 
						s.name as TABLE_SCHEMA,
						t.name as TABLE_NAME, 
						c.name as COLUMN_NAME, 
						d.name as DEFAULT_NAME, 
						d.definition as DEFAULT_VALUE,
						d.is_system_named as IS_SYSTEM_NAMED,
						cast(0 AS bit) AS IS_TYPE
					from sys.tables t 
						inner join sys.columns c on c.object_id = t.object_id
						inner join sys.default_constraints d on c.column_id = d.parent_column_id
							and d.parent_object_id = c.object_id
						inner join sys.schemas s on s.schema_id = t.schema_id
					UNION ALL
					select 
						s.name as TABLE_SCHEMA,
						tt.name as TABLE_NAME, 
						c.name as COLUMN_NAME, 
						d.name as DEFAULT_NAME, 
						d.definition as DEFAULT_VALUE,
						d.is_system_named as IS_SYSTEM_NAMED,
						cast(1 AS bit) AS IS_TYPE
					from sys.table_types tt 
						inner join sys.columns c on c.object_id = tt.type_table_object_id
						inner join sys.default_constraints d on c.column_id = d.parent_column_id
							and d.parent_object_id = c.object_id
						inner join sys.schemas s on s.schema_id = tt.schema_id
";
		using (var dr = cm.ExecuteReader()) {
			while (dr.Read()) {
				var t = FindTable(
					(string)dr["TABLE_NAME"],
					(string)dr["TABLE_SCHEMA"],
					(bool)dr["IS_TYPE"]);
				if (t != null) {
					var c = t.Columns.Find((string)dr["COLUMN_NAME"]);
					if (c != null)
						c.Default = new Default(
							t,
							c,
							(string)dr["DEFAULT_NAME"],
							(string)dr["DEFAULT_VALUE"],
							(bool)dr["IS_SYSTEM_NAMED"]);
				}
			}
		}
	}

	private void LoadColumnIdentities(SqlCommand cm) {
		//get column identities
		cm.CommandText = @"
					select 
						s.name as TABLE_SCHEMA,
						t.name as TABLE_NAME, 
						c.name AS COLUMN_NAME,
						i.SEED_VALUE, i.INCREMENT_VALUE
					from sys.tables t 
						inner join sys.columns c on c.object_id = t.object_id
						inner join sys.identity_columns i on i.object_id = c.object_id
							and i.column_id = c.column_id
						inner join sys.schemas s on s.schema_id = t.schema_id ";
		using (var dr = cm.ExecuteReader()) {
			while (dr.Read())
				try {
					var t = FindTable((string)dr["TABLE_NAME"], (string)dr["TABLE_SCHEMA"]);
					var c = t.Columns.Find((string)dr["COLUMN_NAME"]);
					var seed = dr["SEED_VALUE"].ToString();
					var increment = dr["INCREMENT_VALUE"].ToString();
					c.Identity = new Identity(seed, increment);
				} catch (Exception ex) {
					throw new ApplicationException(
						string.Format(
							"{0}.{1} : {2}",
							dr["TABLE_SCHEMA"],
							dr["TABLE_NAME"],
							ex.Message),
						ex);
				}
		}
	}

	private void LoadColumns(SqlCommand cm) {
		//get columns
		cm.CommandText = @"
				select 
					t.TABLE_SCHEMA,
					c.TABLE_NAME,
					c.COLUMN_NAME,
					c.DATA_TYPE,
					c.ORDINAL_POSITION,
					c.IS_NULLABLE,
					c.CHARACTER_MAXIMUM_LENGTH,
					c.NUMERIC_PRECISION,
					c.NUMERIC_SCALE,
					CASE WHEN COLUMNPROPERTY(OBJECT_ID(c.TABLE_SCHEMA + '.' + c.TABLE_NAME), c.COLUMN_NAME, 'IsRowGuidCol') = 1 THEN 'YES' ELSE 'NO' END AS IS_ROW_GUID_COL
				from INFORMATION_SCHEMA.COLUMNS c
					inner join INFORMATION_SCHEMA.TABLES t
							on t.TABLE_NAME = c.TABLE_NAME
								and t.TABLE_SCHEMA = c.TABLE_SCHEMA
								and t.TABLE_CATALOG = c.TABLE_CATALOG
				where
					t.TABLE_TYPE = 'BASE TABLE'
				order by t.TABLE_SCHEMA, c.TABLE_NAME, c.ORDINAL_POSITION
";
		using (var dr = cm.ExecuteReader()) {
			LoadColumnsBase(dr, Tables);
		}

		try {
			cm.CommandText = @"
				select 
					s.name as TABLE_SCHEMA,
					tt.name as TABLE_NAME, 
					c.name as COLUMN_NAME,
					t.name as DATA_TYPE,
					c.column_id as ORDINAL_POSITION,
					CASE WHEN c.is_nullable = 1 THEN 'YES' ELSE 'NO' END as IS_NULLABLE,
					CASE WHEN t.name = 'nvarchar' and c.max_length > 0 THEN CAST(c.max_length as int)/2 ELSE CAST(c.max_length as int) END as CHARACTER_MAXIMUM_LENGTH,
					c.precision as NUMERIC_PRECISION,
					CAST(c.scale as int) as NUMERIC_SCALE,
					CASE WHEN c.is_rowguidcol = 1 THEN 'YES' ELSE 'NO' END as IS_ROW_GUID_COL
				from sys.columns c
					inner join sys.table_types tt
						on tt.type_table_object_id = c.object_id
					inner join sys.schemas s
						on tt.schema_id = s.schema_id 
					inner join sys.types t
						on t.system_type_id = c.system_type_id
							and t.user_type_id = c.user_type_id
				where
					tt.is_user_defined = 1
				order by s.name, tt.name, c.column_id
";
			using (var dr = cm.ExecuteReader()) {
				LoadColumnsBase(dr, TableTypes);
			}
		} catch (SqlException ex) {
			// todo - detect this before query and get rid of try catch
			_logger.LogError(
				"Assuming sql server version doesn't support table types because"
				+ $"the followign error occurred: {ex.Message}");
		}
	}

	private static void LoadColumnsBase(IDataReader dr, List<Table> tables) {
		Table table = null;

		while (dr.Read()) {
			var c = new Column {
				Name = (string)dr["COLUMN_NAME"],
				Type = (string)dr["DATA_TYPE"],
				IsNullable = (string)dr["IS_NULLABLE"] == "YES",
				Position = (int)dr["ORDINAL_POSITION"],
				IsRowGuidCol = (string)dr["IS_ROW_GUID_COL"] == "YES"
			};

			switch (c.Type) {
				case "binary":
				case "char":
				case "nchar":
				case "nvarchar":
				case "varbinary":
				case "varchar":
					c.Length = (int)dr["CHARACTER_MAXIMUM_LENGTH"];
					break;
				case "decimal":
				case "numeric":
					c.Precision = (byte)dr["NUMERIC_PRECISION"];
					c.Scale = (int)dr["NUMERIC_SCALE"];
					break;
			}

			if (table == null || table.Name != (string)dr["TABLE_NAME"] ||
			    table.Owner != (string)dr["TABLE_SCHEMA"])
				// only do a lookup if the table we have isn't already the relevant one
				table = FindTableBase(
					tables,
					(string)dr["TABLE_NAME"],
					(string)dr["TABLE_SCHEMA"]);

			table.Columns.Add(c);
		}
	}

	private void LoadTables(SqlCommand cm) {
		//get tables
		cm.CommandText = @"
				select
					TABLE_SCHEMA,
					TABLE_NAME
				from INFORMATION_SCHEMA.TABLES
				where TABLE_TYPE = 'BASE TABLE'";
		using (var dr = cm.ExecuteReader()) {
			LoadTablesBase(dr, false, Tables);
		}

		cm.CommandText = @"
			select
				s.name as TABLE_SCHEMA,
				tt.name as TABLE_NAME
			from sys.table_types tt
			inner join sys.schemas s on tt.schema_id = s.schema_id
			where tt.is_user_defined = 1
			order by s.name, tt.name";
		using (var dr = cm.ExecuteReader()) {
			LoadTablesBase(dr, true, TableTypes);
		}
	}

	private void LoadUserDefinedTypes(SqlCommand cm) {
		//get types
		cm.CommandText = @"
            select
                s.name as 'Type_Schema',
                t.name as 'Type_Name',
                tt.name as 'Base_Type_Name',
                t.max_length as 'Max_Length',
                t.is_nullable as 'Nullable'	
            from sys.types t
            inner join sys.schemas s on s.schema_id = t.schema_id
            inner join sys.types tt on t.system_type_id = tt.user_type_id
            where
                t.is_user_defined = 1
            and t.is_table_type = 0";

		using (var dr = cm.ExecuteReader()) {
			LoadUserDefinedTypesBase(dr, UserDefinedTypes);
		}
	}

	private void LoadUserDefinedTypesBase(
		SqlDataReader dr,
		List<UserDefinedType> userDefinedTypes) {
		while (dr.Read())
			userDefinedTypes.Add(
				new UserDefinedType(
					(string)dr["Type_Schema"],
					(string)dr["Type_Name"],
					(string)dr["Base_Type_Name"],
					Convert.ToInt16(dr["Max_Length"]),
					(bool)dr["Nullable"]));
	}

	private static void LoadTablesBase(
		SqlDataReader dr,
		bool areTableTypes,
		List<Table> tables) {
		while (dr.Read())
			tables.Add(
				new Table((string)dr["TABLE_SCHEMA"], (string)dr["TABLE_NAME"])
					{ IsType = areTableTypes });
	}

	private void LoadSchemas(SqlCommand cm) {
		//get schemas
		cm.CommandText = @"
					select s.name as schemaName, p.name as principalName
					from sys.schemas s
					inner join sys.database_principals p on s.principal_id = p.principal_id
					where s.schema_id < 16384
					and s.name not in ('dbo','guest','sys','INFORMATION_SCHEMA')
					order by schema_id
";
		using (var dr = cm.ExecuteReader()) {
			while (dr.Read())
				Schemas.Add(new Schema((string)dr["schemaName"], (string)dr["principalName"]));
		}
	}

	private void LoadProps(SqlCommand cm) {
		var cnStrBuilder = new SqlConnectionStringBuilder(Connection);
		// query schema for database properties
		cm.CommandText = @"
select
	[compatibility_level],
	[collation_name],
	[is_auto_close_on],
	[is_auto_shrink_on],
	[snapshot_isolation_state],
	[is_read_committed_snapshot_on],
	[recovery_model_desc],
	[page_verify_option_desc],
	[is_auto_create_stats_on],
	[is_auto_update_stats_on],
	[is_auto_update_stats_async_on],
	[is_ansi_null_default_on],
	[is_ansi_nulls_on],
	[is_ansi_padding_on],
	[is_ansi_warnings_on],
	[is_arithabort_on],
	[is_concat_null_yields_null_on],
	[is_numeric_roundabort_on],
	[is_quoted_identifier_on],
	[is_recursive_triggers_on],
	[is_cursor_close_on_commit_on],
	[is_local_cursor_default],
	[is_trustworthy_on],
	[is_db_chaining_on],
	[is_parameterization_forced],
	[is_date_correlation_on]
from sys.databases
where name = @dbname
";
		cm.Parameters.AddWithValue("@dbname", cnStrBuilder.InitialCatalog);
		using (IDataReader dr = cm.ExecuteReader()) {
			if (dr.Read()) {
				SetPropString("COMPATIBILITY_LEVEL", dr["compatibility_level"]);
				SetPropString("COLLATE", dr["collation_name"]);
				SetPropOnOff("AUTO_CLOSE", dr["is_auto_close_on"]);
				SetPropOnOff("AUTO_SHRINK", dr["is_auto_shrink_on"]);
				if (dr["snapshot_isolation_state"] != DBNull.Value)
					FindProp("ALLOW_SNAPSHOT_ISOLATION").Value =
						(byte)dr["snapshot_isolation_state"] == 0 ||
						(byte)dr["snapshot_isolation_state"] == 2
							? "OFF"
							: "ON";

				SetPropOnOff("READ_COMMITTED_SNAPSHOT", dr["is_read_committed_snapshot_on"]);
				SetPropString("RECOVERY", dr["recovery_model_desc"]);
				SetPropString("PAGE_VERIFY", dr["page_verify_option_desc"]);
				SetPropOnOff("AUTO_CREATE_STATISTICS", dr["is_auto_create_stats_on"]);
				SetPropOnOff("AUTO_UPDATE_STATISTICS", dr["is_auto_update_stats_on"]);
				SetPropOnOff(
					"AUTO_UPDATE_STATISTICS_ASYNC",
					dr["is_auto_update_stats_async_on"]);
				SetPropOnOff("ANSI_NULL_DEFAULT", dr["is_ansi_null_default_on"]);
				SetPropOnOff("ANSI_NULLS", dr["is_ansi_nulls_on"]);
				SetPropOnOff("ANSI_PADDING", dr["is_ansi_padding_on"]);
				SetPropOnOff("ANSI_WARNINGS", dr["is_ansi_warnings_on"]);
				SetPropOnOff("ARITHABORT", dr["is_arithabort_on"]);
				SetPropOnOff("CONCAT_NULL_YIELDS_NULL", dr["is_concat_null_yields_null_on"]);
				SetPropOnOff("NUMERIC_ROUNDABORT", dr["is_numeric_roundabort_on"]);
				SetPropOnOff("QUOTED_IDENTIFIER", dr["is_quoted_identifier_on"]);
				SetPropOnOff("RECURSIVE_TRIGGERS", dr["is_recursive_triggers_on"]);
				SetPropOnOff("CURSOR_CLOSE_ON_COMMIT", dr["is_cursor_close_on_commit_on"]);
				if (dr["is_local_cursor_default"] != DBNull.Value)
					FindProp("CURSOR_DEFAULT").Value =
						(bool)dr["is_local_cursor_default"] ? "LOCAL" : "GLOBAL";

				SetPropOnOff("TRUSTWORTHY", dr["is_trustworthy_on"]);
				SetPropOnOff("DB_CHAINING", dr["is_db_chaining_on"]);
				if (dr["is_parameterization_forced"] != DBNull.Value)
					FindProp("PARAMETERIZATION").Value =
						(bool)dr["is_parameterization_forced"] ? "FORCED" : "SIMPLE";

				SetPropOnOff("DATE_CORRELATION_OPTIMIZATION", dr["is_date_correlation_on"]);
			}
		}
	}

	#endregion

	#region Script

	public void ScriptToDir(string tableHint = null, Action<TraceLevel, string> log = null) {
		if (log == null) log = (tl, s) => { };

		if (Directory.Exists(Dir)) {
			// delete the existing script files
			log(TraceLevel.Verbose, "Deleting existing files...");

			var files = Dirs.Select(dir => Path.Combine(Dir, dir))
				.Where(Directory.Exists)
				.SelectMany(Directory.GetFiles);
			foreach (var f in files) File.Delete(f);

			log(TraceLevel.Verbose, "Existing files deleted.");
		} else {
			Directory.CreateDirectory(Dir);
		}

		WritePropsScript(log);
		WriteScriptDir("schemas", Schemas.ToArray(), log);
		WriteScriptDir("tables", Tables.ToArray(), log);
		foreach (var table in Tables) {
			WriteScriptDir(
				"check_constraints",
				table.Constraints.Where(c => c.Type == "CHECK").ToArray(),
				log);

			var defaults = (from c in table.Columns.Items
				where c.Default != null
				select c.Default).ToArray();

			if (defaults.Any()) WriteScriptDir("defaults", defaults, log);
		}

		WriteScriptDir("table_types", TableTypes.ToArray(), log);
		WriteScriptDir("user_defined_types", UserDefinedTypes.ToArray(), log);
		WriteScriptDir(
			"foreign_keys",
			ForeignKeys.OrderBy(x => x, ForeignKeyComparer.Instance).ToArray(),
			log);
		foreach (var routineType in Routines.GroupBy(x => x.RoutineType)) {
			var dir = routineType.Key.ToString().ToLower() + "s";
			WriteScriptDir(dir, routineType.ToArray(), log);
		}

		WriteScriptDir("views", ViewIndexes.ToArray(), log);
		WriteScriptDir("assemblies", Assemblies.ToArray(), log);
		WriteScriptDir("roles", Roles.ToArray(), log);
		WriteScriptDir("users", Users.ToArray(), log);
		WriteScriptDir("synonyms", Synonyms.ToArray(), log);
		WriteScriptDir("permissions", Permissions.ToArray(), log);

		ExportData(tableHint, log);
	}

	private void WritePropsScript(Action<TraceLevel, string> log) {
		if (!Dirs.Contains("props")) return;
		log(TraceLevel.Verbose, "Scripting database properties...");
		var text = new StringBuilder();
		text.Append(ScriptPropList(Props));
		text.AppendLine("GO");
		text.AppendLine();
		File.WriteAllText($"{Dir}/props.sql", text.ToString());
	}

	private void WriteScriptDir(
		string name,
		ICollection<IScriptable> objects,
		Action<TraceLevel, string> log) {
		if (!objects.Any()) return;
		if (!Dirs.Contains(name)) return;
		var dir = Path.Combine(Dir, name);
		Directory.CreateDirectory(dir);
		var index = 0;
		foreach (var o in objects) {
			log(
				TraceLevel.Verbose,
				$"Scripting {name} {++index} of {objects.Count}...{(index < objects.Count ? "\r" : string.Empty)}");
			var filePath = Path.Combine(dir, MakeFileName(o) + ".sql");
			var script = o.ScriptCreate() + "\r\nGO\r\n";
			File.AppendAllText(filePath, script);
		}
	}

	private static string MakeFileName(object o) {
		// combine foreign keys into one script per table
		var fk = o as ForeignKey;
		if (fk != null) return MakeFileName(fk.Table);

		// combine defaults into one script per table
		if (o is Default) return MakeFileName((o as Default).Table);

		// combine check constraints into one script per table
		if (o is Constraint && (o as Constraint).Type == "CHECK")
			return MakeFileName((o as Constraint).Table);

		var schema = o as IHasOwner == null ? "" : (o as IHasOwner).Owner;
		var name = o as INameable == null ? "" : (o as INameable).Name;

		var fileName = MakeFileName(schema, name);

		// prefix user defined types with TYPE_
		var prefix = o as Table == null ? "" : (o as Table).IsType ? "TYPE_" : "";

		return string.Concat(prefix, fileName);
	}

	private static string MakeFileName(string schema, string name) {
		// Dont' include schema name for objects in the dbo schema.
		// This maintains backward compatability for those who use
		// SchemaZen to keep their schemas under version control.
		var fileName = name;
		if (!string.IsNullOrEmpty(schema) && schema.ToLower() != "dbo")
			fileName = $"{schema}.{name}";

		return Path.GetInvalidFileNameChars()
			.Aggregate(
				fileName,
				(current, invalidChar) => current.Replace(invalidChar, '-'));
	}

	public void ExportData(string tableHint = null, Action<TraceLevel, string> log = null) {
		if (!DataTables.Any())
			return;
		var dataDir = Dir + "/data";
		if (!Directory.Exists(dataDir)) Directory.CreateDirectory(dataDir);

		log?.Invoke(TraceLevel.Info, "Exporting data...");
		var index = 0;
		foreach (var t in DataTables) {
			log?.Invoke(
				TraceLevel.Verbose,
				$"Exporting data from {t.Owner + "." + t.Name} (table {++index} of {DataTables.Count})...");
			var filePathAndName = dataDir + "/" + MakeFileName(t) + ".tsv";
			var sw = File.CreateText(filePathAndName);
			t.ExportData(Connection, sw, tableHint);

			sw.Flush();
			if (sw.BaseStream.Length == 0) {
				log?.Invoke(
					TraceLevel.Verbose,
					$"          No data to export for {t.Owner + "." + t.Name}, deleting file...");
				sw.Close();
				File.Delete(filePathAndName);
			} else {
				sw.Close();
			}
		}
	}

	public static string ScriptPropList(IList<DbProp> props) {
		var text = new StringBuilder();

		text.AppendLine("DECLARE @DB VARCHAR(255)");
		text.AppendLine("SET @DB = DB_NAME()");
		foreach (var p in props.Select(p => p.Script()).Where(p => !string.IsNullOrEmpty(p)))
			text.AppendLine(p);

		return text.ToString();
	}

	#endregion

	#region Create

	public void ImportData(Action<TraceLevel, string> log = null) {
		if (log == null) log = (tl, s) => { };

		var dataDir = Dir + "\\data";
		if (!Directory.Exists(dataDir)) {
			log(TraceLevel.Verbose, "No data to import.");
			return;
		}

		log(TraceLevel.Verbose, "Loading database schema...");
		Load(); // load the schema first so we can import data
		log(TraceLevel.Verbose, "Database schema loaded.");
		log(TraceLevel.Info, "Importing data...");

		foreach (var f in Directory.GetFiles(dataDir)) {
			var fi = new FileInfo(f);
			var schema = "dbo";
			var table = Path.GetFileNameWithoutExtension(fi.Name);
			if (table.Contains(".")) {
				schema = fi.Name.Split('.')[0];
				table = fi.Name.Split('.')[1];
			}

			var t = FindTable(table, schema);
			if (t == null) {
				log(
					TraceLevel.Warning,
					$"Warning: found data file '{fi.Name}', but no corresponding table in database...");
				continue;
			}

			try {
				log(TraceLevel.Verbose, $"Importing data for table {schema}.{table}...");
				t.ImportData(Connection, fi.FullName);
			} catch (SqlBatchException ex) {
				throw new DataFileException(ex.Message, fi.FullName, ex.LineNumber);
			} catch (Exception ex) {
				throw new DataFileException(ex.Message, fi.FullName, -1);
			}
		}

		log(TraceLevel.Info, "Data imported successfully.");
	}

	public void CreateFromDir(
		bool overwrite,
		string databaseFilesPath = null,
		Action<TraceLevel, string> log = null) {
		if (log == null) log = (tl, s) => { };

		if (DBHelper.DbExists(Connection)) {
			_logger?.LogTrace("Dropping existing database...");
			DBHelper.DropDb(Connection);
			_logger?.LogTrace("Existing database dropped.");
		}

		_logger?.LogTrace("Creating database...");
		DBHelper.CreateDb(Connection, databaseFilesPath);

		//run scripts
		if (File.Exists(Dir + "/props.sql")) {
			_logger?.LogTrace("Setting database properties...");
			try {
				DBHelper.ExecBatchSql(Connection, File.ReadAllText(Dir + "/props.sql"));
			} catch (SqlBatchException ex) {
				throw new SqlFileException(Dir + "/props.sql", ex);
			}

			// COLLATE can cause connection to be reset
			// so clear the pool so we get a new connection
			DBHelper.ClearPool(Connection);
		}

		// stage 0: everything not in another stage
		// stage 1: after_data & foreign keys
		_logger?.LogTrace("Creating database objects...");
		var stages = GetScriptStages();
		for (var i = 0; i < stages.Count; i++) {
			if (i == 2) ImportData(log); // load data before stage 2

			_logger?.LogTrace($"running stage {i}");
			var errors = RunStage(GetScripts(stages[i]));

			if (errors.Count > 0) {
				_logger?.LogCritical("Aborting due to unresolved errors");
				var ex = new BatchSqlFileException {
					Exceptions = errors
				};
				throw ex;
			}
		}
	}

	private List<HashSet<string>> GetScriptStages() {
		// stage zero objects must never have dependencies
		var stage0 = new HashSet<string> { "roles" };

		// runs after most objects have been created
		// permissions are inlcued here because they tie a user to another object
		var stage2 = new HashSet<string> { "permissions" };

		// stage3 runs after data has been imported
		var stage3 = new HashSet<string> { "after_data", "foreign_keys" };

		var stage1 = Dirs
			.Except(stage0)
			.Except(stage2)
			.Except(stage3)
			.ToHashSet();

		var itemsByStage = new List<HashSet<string>> { stage0, stage1, stage2, stage3 };

		foreach (var stage in itemsByStage)
			stage.RemoveWhere(
				x => {
					var path = Path.Combine(Dir, x);
					return !Directory.Exists(path) && !File.Exists(path);
				});

		return itemsByStage;
	}

	private List<string> GetScripts(HashSet<string> items) {
		var scripts = new List<string>();
		foreach (var item in items) {
			var path = Path.Combine(Dir, item);
			if (item.EndsWith(".sql"))
				scripts.Add(path);
			else
				scripts.AddRange(Directory.GetFiles(path, "*.sql"));
		}

		return scripts;
	}

	private List<SqlFileException> RunStage(List<string> scripts) {
		// resolve dependencies by trying over and over
		// if the number of failures stops decreasing then give up
		var errors = new List<SqlFileException>();
		var prevCount = -1;
		var attempt = 1;
		while (scripts.Count > 0 && (prevCount == -1 || errors.Count < prevCount)) {
			if (errors.Count > 0) {
				prevCount = errors.Count;
				attempt++;
				_logger?.LogTrace($"{errors.Count} errors occurred, retrying...");
			}

			errors.Clear();
			var index = 0;
			var total = scripts.Count;
			foreach (var f in scripts.ToArray()) {
				_logger?.LogTrace(
					$"Executing script {++index} of {total}...{(index < total ? "\r" : string.Empty)}");
				_logger?.LogTrace($"file name: {f}");
				try {
					DBHelper.ExecBatchSql(Connection, File.ReadAllText(f));
					scripts.Remove(f);
				} catch (SqlBatchException ex) {
					_logger?.LogTrace($"attempt {attempt}: {f}@{ex.LineNumber} {ex.Message}");
					errors.Add(new SqlFileException(f, ex));
				}
			}
		}

		if (errors.Any())
			foreach (var error in errors)
				_logger?.LogError(error.Message);

		return errors;
	}

	#endregion
}
