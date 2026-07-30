using Microsoft.SqlServer.Management.Smo;
using Microsoft.SqlServer.Management.Smo.RegSvrEnum;
using Microsoft.SqlServer.Management.UI.VSIntegration;
using Microsoft.SqlServer.Management.UI.VSIntegration.ObjectExplorer;
using Microsoft.SqlServer.Management.Common;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Microsoft.Data.SqlClient;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml;
using System.Text.RegularExpressions;
using EnvDTE;
using Microsoft.VisualStudio.Shell;

namespace AxialSqlTools
{
    public static class ScriptFactoryAccess
    {
        public class ConnectionInfo
        {
            private string fullConnectionString;

            public string FullConnectionString
            {
                get { return fullConnectionString; }
                set { fullConnectionString = EnsureTrustServerCertificate(value); }
            }
            public string Database { get; set; }
            public string ServerName { get; set; }
            public UIConnectionInfo ActiveConnectionInfo { get; set; }

            public string DisplayName
            {
                get
                {
                    return $"[{ServerName}] \\ [{Database}]";
                }
            }

            public override string ToString() => DisplayName;
        }

        /// <summary>
        /// Microsoft.Data.SqlClient defaults Encrypt=true. Force TrustServerCertificate so
        /// self-signed / internal SQL Server certs work across all plugin features.
        /// </summary>
        public static string EnsureTrustServerCertificate(string connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                return connectionString;
            }

            try
            {
                var builder = new SqlConnectionStringBuilder(connectionString);
                ApplyTrustServerCertificate(builder);
                return builder.ConnectionString;
            }
            catch
            {
                return connectionString;
            }
        }

        public static void ApplyTrustServerCertificate(SqlConnectionStringBuilder builder)
        {
            if (builder == null)
            {
                return;
            }

            builder.TrustServerCertificate = true;
        }

        private static INodeInformation GetSelectedNode(IObjectExplorerService _objectExplorerService)
        {
            INodeInformation[] nodes;
            int nodeCount;
            _objectExplorerService.GetSelectedNodes(out nodeCount, out nodes);

            return (nodeCount > 0 ? nodes[0] : null);
        }

        public static List<ConnectionInfo> GetConnectedObjectExplorerSessions()
        {
            var results = new List<ConnectionInfo>();
            var seenServers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                var oeService = (IObjectExplorerService)ServiceCache.ServiceProvider.GetService(typeof(IObjectExplorerService));
                if (oeService == null)
                {
                    return results;
                }

                foreach (INodeInformation rootNode in EnumerateConnectedRootNodes(oeService))
                {
                    ConnectionInfo ci = BuildConnectionInfoFromNode(rootNode, inMaster: true);
                    if (ci == null || string.IsNullOrWhiteSpace(ci.ServerName))
                    {
                        continue;
                    }

                    if (seenServers.Add(ci.ServerName))
                    {
                        results.Add(ci);
                    }
                }

                if (results.Count == 0)
                {
                    ConnectionInfo selected = GetCurrentConnectionInfoFromObjectExplorer(inMaster: true);
                    if (selected != null && !string.IsNullOrWhiteSpace(selected.ServerName))
                    {
                        results.Add(selected);
                    }
                }
            }
            catch (Exception)
            {
                ConnectionInfo selected = GetCurrentConnectionInfoFromObjectExplorer(inMaster: true);
                if (selected != null && !string.IsNullOrWhiteSpace(selected.ServerName))
                {
                    results.Add(selected);
                }
            }

            return results;
        }

        private static IEnumerable<INodeInformation> EnumerateConnectedRootNodes(IObjectExplorerService oeService)
        {
            TreeView treeView = TryGetObjectExplorerTreeView(oeService);
            if (treeView != null)
            {
                foreach (TreeNode root in treeView.Nodes)
                {
                    INodeInformation info = GetNodeInformationFromTreeNode(root);
                    if (info?.Connection != null)
                    {
                        yield return info;
                    }
                }

                yield break;
            }

            foreach (INodeInformation info in EnumerateConnectedRootNodesFromHierarchies(oeService))
            {
                yield return info;
            }
        }

        private static TreeView TryGetObjectExplorerTreeView(IObjectExplorerService oeService)
        {
            try
            {
                PropertyInfo treeProperty = oeService.GetType().GetProperty(
                    "Tree",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.IgnoreCase);
                if (treeProperty == null)
                {
                    return null;
                }

                return treeProperty.GetValue(oeService, null) as TreeView;
            }
            catch
            {
                return null;
            }
        }

        private static INodeInformation GetNodeInformationFromTreeNode(TreeNode node)
        {
            if (!(node is IServiceProvider serviceProvider))
            {
                return null;
            }

            return serviceProvider.GetService(typeof(INodeInformation)) as INodeInformation;
        }

        private static IEnumerable<INodeInformation> EnumerateConnectedRootNodesFromHierarchies(IObjectExplorerService oeService)
        {
            object tree;
            try
            {
                PropertyInfo treeProperty = oeService.GetType().GetProperty(
                    "Tree",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.IgnoreCase);
                tree = treeProperty?.GetValue(oeService, null);
            }
            catch
            {
                yield break;
            }

            if (tree == null)
            {
                yield break;
            }

            object hierarchies;
            try
            {
                PropertyInfo hierarchiesProperty = tree.GetType().GetProperty(
                    "Hierarchies",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.IgnoreCase);
                hierarchies = hierarchiesProperty?.GetValue(tree, null);
            }
            catch
            {
                yield break;
            }

            if (!(hierarchies is System.Collections.IEnumerable enumerable))
            {
                yield break;
            }

            foreach (object entry in enumerable)
            {
                object hierarchy = entry;
                if (entry is System.Collections.DictionaryEntry dictionaryEntry)
                {
                    hierarchy = dictionaryEntry.Value;
                }
                else
                {
                    PropertyInfo valueProperty = entry?.GetType().GetProperty("Value");
                    if (valueProperty != null)
                    {
                        hierarchy = valueProperty.GetValue(entry, null);
                    }
                }

                if (hierarchy == null)
                {
                    continue;
                }

                INodeInformation root = TryGetHierarchyRoot(hierarchy);
                if (root?.Connection != null)
                {
                    yield return root;
                }
            }
        }

        private static INodeInformation TryGetHierarchyRoot(object hierarchy)
        {
            string[] propertyNames = { "Root", "RootNode", "ConnectionNode", "ServerNode" };
            foreach (string propertyName in propertyNames)
            {
                try
                {
                    PropertyInfo property = hierarchy.GetType().GetProperty(
                        propertyName,
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
                    object value = property?.GetValue(hierarchy, null);
                    if (value is INodeInformation node)
                    {
                        return node;
                    }

                    if (value is TreeNode treeNode)
                    {
                        INodeInformation fromTree = GetNodeInformationFromTreeNode(treeNode);
                        if (fromTree != null)
                        {
                            return fromTree;
                        }
                    }
                }
                catch
                {
                }
            }

            return null;
        }

        public static List<string> GetDatabases(ConnectionInfo connectionInfo)
        {
            var list = new List<string>();
            if (connectionInfo == null || string.IsNullOrWhiteSpace(connectionInfo.FullConnectionString))
            {
                return list;
            }

            SqlConnectionStringBuilder builder = new SqlConnectionStringBuilder(connectionInfo.FullConnectionString)
            {
                InitialCatalog = "master"
            };

            using (var conn = new SqlConnection(builder.ConnectionString))
            {
                conn.Open();
                const string sql = @"
SELECT [name]
FROM sys.databases
WHERE [name] <> 'tempdb'
  AND [state] = 0
  AND [user_access] = 0
  AND HAS_DBACCESS([name]) = 1
ORDER BY [name];";

                using (var cmd = new SqlCommand(sql, conn))
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        list.Add(reader.GetString(0));
                    }
                }
            }

            return list;
        }

        public static ConnectionInfo GetCurrentConnectionInfoFromObjectExplorer(bool inMaster = false)
        {
            var oeService = (IObjectExplorerService)ServiceCache.ServiceProvider.GetService(typeof(IObjectExplorerService));
            if (oeService == null)
                return null;

            var selectedNode = GetSelectedNode(oeService);
            if (selectedNode == null)
                return null;

            return BuildConnectionInfoFromNode(selectedNode, inMaster);
        }

        private static ConnectionInfo BuildConnectionInfoFromNode(INodeInformation selectedNode, bool inMaster)
        {
            if (selectedNode?.Connection == null)
                return null;

            string databaseName = "master";
            if (!inMaster)
            {
                Match match = Regex.Match(selectedNode.Context ?? string.Empty, @"Database\[@Name='(.*?)'\]");
                if (match.Success)
                {
                    databaseName = match.Groups[1].Value;
                }
            }

            var objectExplorerConnection = selectedNode.Connection;
            string userName = objectExplorerConnection.UserName;
            string password = objectExplorerConnection.Password;
            string auth = GetAuthenticationMode(objectExplorerConnection);

            var builder = new SqlConnectionStringBuilder
            {
                DataSource = objectExplorerConnection.ServerName,
                InitialCatalog = databaseName,
                ApplicationName = "Axial SQL Tools"
            };

            ApplyAuthentication(builder, userName, password, auth);

            if (objectExplorerConnection is SqlConnectionInfo sqlConnectionInfo)
            {
                builder.Encrypt = sqlConnectionInfo.EncryptConnection;
            }

            ApplyTrustServerCertificate(builder);

            ConnectionInfo ci = new ConnectionInfo();
            ci.FullConnectionString = builder.ToString();
            ci.Database = databaseName;
            ci.ServerName = builder.DataSource;
            ci.ActiveConnectionInfo = TryCreateUiConnectionInfo(builder, userName, password, auth);

            return ci;
        }

        public static UIConnectionInfo TryCreateUiConnectionInfo(
            SqlConnectionStringBuilder builder,
            string userName,
            string password,
            string auth)
        {
            if (builder == null || string.IsNullOrWhiteSpace(builder.DataSource))
            {
                return null;
            }

            try
            {
                var ui = new UIConnectionInfo
                {
                    ServerName = builder.DataSource,
                    UserName = userName ?? string.Empty,
                    Password = password ?? string.Empty
                };

                SetAdvancedOption(ui, "DATABASE", builder.InitialCatalog ?? "master");
                SetAdvancedOption(ui, "ENCRYPT_CONNECTION", builder.Encrypt ? "True" : "False");
                SetAdvancedOption(ui, "TRUST_SERVER_CERTIFICATE", "True");

                if (!string.IsNullOrWhiteSpace(auth))
                {
                    ui.OtherParams = auth;
                }

                return ui;
            }
            catch
            {
                return null;
            }
        }

        private static void SetAdvancedOption(UIConnectionInfo connection, string key, string value)
        {
            if (connection?.AdvancedOptions == null || string.IsNullOrWhiteSpace(key))
            {
                return;
            }

            try
            {
                connection.AdvancedOptions[key] = value;
                return;
            }
            catch
            {
            }

            try
            {
                MethodInfo setMethod = connection.AdvancedOptions.GetType().GetMethod(
                    "Set",
                    BindingFlags.Instance | BindingFlags.Public,
                    null,
                    new[] { typeof(string), typeof(string) },
                    null);
                setMethod?.Invoke(connection.AdvancedOptions, new object[] { key, value });
            }
            catch
            {
            }
        }

        public static ConnectionInfo CloneWithDatabase(ConnectionInfo source, string databaseName)
        {
            if (source == null)
            {
                return null;
            }

            var builder = new SqlConnectionStringBuilder(source.FullConnectionString)
            {
                InitialCatalog = string.IsNullOrWhiteSpace(databaseName) ? "master" : databaseName
            };
            ApplyTrustServerCertificate(builder);

            return new ConnectionInfo
            {
                FullConnectionString = builder.ToString(),
                Database = builder.InitialCatalog,
                ServerName = builder.DataSource,
                ActiveConnectionInfo = source.ActiveConnectionInfo
            };
        }

        public static ConnectionInfo GetCurrentConnectionInfo(bool inMaster = false)
        {
            return GetCurrentConnectionInfoForEditor(null, inMaster);
        }

        /// <summary>优先读取查询编辑器工具栏当前库（m_connection.Database），避免 AdvancedOptions 滞后。</summary>
        public static ConnectionInfo GetCurrentConnectionInfoForEditor(Microsoft.VisualStudio.TextManager.Interop.IVsTextView textView = null, bool inMaster = false)
        {
            var scriptFactory = ServiceCache.ScriptFactory;
            if (scriptFactory == null) return null;

            var connInfo = scriptFactory.CurrentlyActiveWndConnectionInfo;
            if (connInfo == null) return null;

            UIConnectionInfo connection = connInfo.UIConnectionInfo;
            if (connection == null) return null;

            string databaseName = inMaster ? "master" : GetAdvancedOption(connection, "DATABASE");
            if (!inMaster && textView != null
                && GridAccess.TryGetConnectionInfoForTextView(textView, out _, out string viewDatabase))
            {
                databaseName = viewDatabase;
            }
            else if (!inMaster && TryGetQueryEditorDatabase(connInfo, out string editorDatabase))
            {
                databaseName = editorDatabase;
            }
            if (string.IsNullOrWhiteSpace(databaseName))
                databaseName = "master";

            var builder = new SqlConnectionStringBuilder
            {
                DataSource = connection.ServerName,
                InitialCatalog = databaseName,
                ApplicationName = "Axial SQL Tools"
            };

            string auth = GetAuthenticationMode(connection);

            ApplyAuthentication(builder, connection.UserName, connection.Password, auth);

            if (IsTrue(GetAdvancedOption(connection, "ENCRYPT_CONNECTION")))
                builder.Encrypt = true;

            ApplyTrustServerCertificate(builder);

            return new ConnectionInfo
            {
                FullConnectionString = builder.ToString(),
                Database = databaseName,
                ServerName = connection.ServerName,
                ActiveConnectionInfo = connection
            };
        }

        private static bool TryGetQueryEditorDatabase(object activeWndConnectionInfo, out string database)
        {
            database = null;
            if (!TryFindSqlConnectionObject(activeWndConnectionInfo, out object connection))
                return false;
            database = GridAccess.GetProperty(connection, "Database") as string;
            return !string.IsNullOrWhiteSpace(database);
        }

        private static bool TryFindSqlConnectionObject(object root, out object connection)
        {
            connection = null;
            if (root == null)
                return false;

            var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
            var queue = new Queue<object>();
            queue.Enqueue(root);
            int steps = 0;

            while (queue.Count > 0 && steps++ < 250)
            {
                object current = queue.Dequeue();
                if (current == null || !visited.Add(current))
                    continue;

                string database = GridAccess.GetProperty(current, "Database") as string;
                string dataSource = GridAccess.GetProperty(current, "DataSource") as string;
                if (!string.IsNullOrWhiteSpace(database) && !string.IsNullOrWhiteSpace(dataSource))
                {
                    connection = current;
                    return true;
                }

                EnqueueReflectionChildren(current, queue);
            }

            return false;
        }

        private static void EnqueueReflectionChildren(object current, Queue<object> queue)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            Type type = current.GetType();
            if (IsSimpleReflectionType(type))
                return;

            foreach (FieldInfo field in type.GetFields(flags))
            {
                object value = null;
                try { value = field.GetValue(current); } catch { }
                if (value != null)
                    queue.Enqueue(value);
            }

            foreach (PropertyInfo property in type.GetProperties(flags))
            {
                if (!property.CanRead || property.GetIndexParameters().Length > 0)
                    continue;
                object value = null;
                try { value = property.GetValue(current, null); } catch { }
                if (value != null)
                    queue.Enqueue(value);
            }
        }

        private static bool IsSimpleReflectionType(Type type)
        {
            return type == null
                || type.IsPrimitive
                || type.IsEnum
                || type == typeof(string)
                || type == typeof(decimal)
                || type == typeof(DateTime)
                || type == typeof(TimeSpan)
                || type == typeof(Guid);
        }

        private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceEqualityComparer Instance = new ReferenceEqualityComparer();
            public new bool Equals(object x, object y) => ReferenceEquals(x, y);
            public int GetHashCode(object obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }

        private static string GetAdvancedOption(UIConnectionInfo connection, string key)
        {
            if (connection?.AdvancedOptions == null || string.IsNullOrWhiteSpace(key))
                return null;

            return connection.AdvancedOptions.Get(key);
        }

        private static string GetAuthenticationMode(UIConnectionInfo connection)
        {
            if (connection == null)
                return string.Empty;

            var values = new List<string>();

            if (!string.IsNullOrWhiteSpace(connection.OtherParams))
                values.Add(connection.OtherParams);

            if (connection.AdvancedOptions != null)
            {
                foreach (string key in connection.AdvancedOptions.AllKeys)
                {
                    if (!string.IsNullOrWhiteSpace(key))
                        values.Add(key);

                    string value = connection.AdvancedOptions.Get(key);
                    if (!string.IsNullOrWhiteSpace(value))
                        values.Add(value);
                }
            }

            return string.Join(";", values);
        }

        private static string GetAuthenticationMode(object connection)
        {
            if (connection == null)
                return string.Empty;

            var values = new List<string>();

            AddPropertyValue(connection, "Authentication", values);
            AddPropertyValue(connection, "AuthenticationMethod", values);
            AddPropertyValue(connection, "AuthenticationType", values);
            AddPropertyValue(connection, "ConnectionString", values);

            return string.Join(";", values);
        }

        private static void AddPropertyValue(object instance, string propertyName, List<string> values)
        {
            var property = instance.GetType().GetProperty(propertyName);
            if (property == null)
                return;

            object value = null;
            try
            {
                value = property.GetValue(instance, null);
            }
            catch
            {
                return;
            }

            if (value == null)
                return;

            string text = value.ToString();
            if (!string.IsNullOrWhiteSpace(text))
                values.Add(text);
        }

        private static void ApplyAuthentication(SqlConnectionStringBuilder builder, string userName, string password, string auth)
        {
            SqlAuthenticationMethod? authenticationMethod = GetSqlAuthenticationMethod(auth, userName, password);

            if (authenticationMethod.HasValue)
            {
                builder.Authentication = authenticationMethod.Value;

                if (!string.IsNullOrWhiteSpace(userName))
                    builder.UserID = userName;

                if (ShouldSendPassword(authenticationMethod.Value) && !string.IsNullOrWhiteSpace(password))
                    builder.Password = password;

                // Do not set IntegratedSecurity=True for Microsoft Entra.
                // Active Directory / Microsoft Entra authentication is mutually exclusive with SSPI/Kerberos.
            }
            else if (!string.IsNullOrWhiteSpace(password))
            {
                builder.IntegratedSecurity = false;
                builder.UserID = userName;
                builder.Password = password;
            }
            else
            {
                builder.IntegratedSecurity = true;
            }
        }

        private static SqlAuthenticationMethod? GetSqlAuthenticationMethod(string auth, string userName, string password)
        {
            if (ContainsAny(auth, "Active Directory Password", "ActiveDirectoryPassword", "Microsoft Entra Password", "Azure Active Directory Password"))
                return SqlAuthenticationMethod.ActiveDirectoryPassword;

            if (ContainsAny(auth, "Active Directory Integrated", "ActiveDirectoryIntegrated", "Microsoft Entra Integrated", "Azure Active Directory Integrated"))
                return SqlAuthenticationMethod.ActiveDirectoryIntegrated;

            if (ContainsAny(auth, "Active Directory Default", "ActiveDirectoryDefault", "Microsoft Entra Default", "Azure Active Directory Default"))
                return SqlAuthenticationMethod.ActiveDirectoryDefault;

            if (ShouldUseMicrosoftEntraInteractive(userName, password, auth))
                return SqlAuthenticationMethod.ActiveDirectoryInteractive;

            return null;
        }

        private static bool ShouldSendPassword(SqlAuthenticationMethod authenticationMethod)
        {
            return authenticationMethod == SqlAuthenticationMethod.ActiveDirectoryPassword ||
                   authenticationMethod == SqlAuthenticationMethod.SqlPassword;
        }

        private static bool ContainsAny(string value, params string[] tokens)
        {
            if (string.IsNullOrWhiteSpace(value) || tokens == null)
                return false;

            foreach (string token in tokens)
            {
                if (!string.IsNullOrWhiteSpace(token) &&
                    value.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool ShouldUseMicrosoftEntraInteractive(string userName, string password, string auth)
        {
            if (IsMicrosoftEntraMfa(auth))
                return true;

            bool hasUserName = !string.IsNullOrWhiteSpace(userName);
            bool hasPassword = !string.IsNullOrWhiteSpace(password);

            // Microsoft Entra MFA commonly has a UPN username and no password.
            // This prevents accidental fallback to Integrated Security=True / SSPI.
            if (hasUserName && !hasPassword && LooksLikeUpn(userName))
                return true;

            return false;
        }

        private static bool IsMicrosoftEntraMfa(string auth)
        {
            if (string.IsNullOrWhiteSpace(auth))
                return false;

            return auth.IndexOf("Active Directory Interactive", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   auth.IndexOf("ActiveDirectoryInteractive", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   auth.IndexOf("Microsoft Entra MFA", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   auth.IndexOf("Microsoft Entra", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   auth.IndexOf("Azure Active Directory - Universal with MFA", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   auth.IndexOf("Universal with MFA", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   auth.IndexOf("MFA", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool LooksLikeUpn(string userName)
        {
            if (string.IsNullOrWhiteSpace(userName))
                return false;

            return userName.Contains("@") && !userName.Contains("\\");
        }

        private static bool IsTrue(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;

            return value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                   value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
                   value.Equals("1", StringComparison.OrdinalIgnoreCase);
        }

        public static string GetActiveQueryWindowText()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                DTE application = ServiceCache.ExtensibilityModel?.Application;
                if (application == null || application.ActiveDocument == null)
                {
                    return string.Empty;
                }

                TextDocument doc = application.ActiveDocument.Object("TextDocument") as TextDocument;
                if (doc == null)
                {
                    return string.Empty;
                }

                EditPoint startPoint = doc.StartPoint.CreateEditPoint();
                return startPoint.GetText(doc.EndPoint);
            }
            catch
            {
                return string.Empty;
            }
        }

        public static string GetXmlFromUIConnectionInfo(UIConnectionInfo connectionInfo)
        {
            if (connectionInfo == null)
            {
                throw new ArgumentNullException(nameof(connectionInfo), "ConnectionInfo cannot be null.");
            }

            StringBuilder sb = new StringBuilder();

            using (XmlWriter writer = XmlWriter.Create(sb))
            {
                connectionInfo.SaveToStream(writer, saveName: true);
            }

            return sb.ToString();
        }

        public static UIConnectionInfo CreateConnectionInfoFromXml(string xmlString)
        {
            if (string.IsNullOrEmpty(xmlString))
            {
                throw new ArgumentNullException(nameof(xmlString), "The XML string cannot be null or empty.");
            }

            UIConnectionInfo connectionInfo = null;

            using (var xmlReader = XmlReader.Create(new StringReader(xmlString)))
            {
                xmlReader.MoveToContent();
                connectionInfo = UIConnectionInfo.LoadFromStream(xmlReader);
            }

            return connectionInfo;
        }

    }
}
