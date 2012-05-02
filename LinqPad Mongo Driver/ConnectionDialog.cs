﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using System.Xml.Linq;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;

namespace GDSX.Externals.LinqPad.Driver
{
    public partial class ConnectionDialog : Form
    {
        private readonly ConnectionProperties mConnection;
        private readonly bool mIsNewConnection;

        private bool mCloseAlreadyValidated = false;

        private Dictionary<Assembly, string> LoadedAssemblies = new Dictionary<Assembly, string>();
        private readonly Dictionary<Type, Type> SerializerMappings = new Dictionary<Type, Type>();
        private readonly SortedSet<Type> LoadedTypes = new SortedSet<Type>(
            MongoDynamicDataContextDriver.CommonTypes.Values,
            new DynamicComparer<Type>((t1, t2) => string.Compare(t1.FullName, t2.FullName, false)));
                                                  
        private readonly Func<string, Assembly> loadSafely;

        private Dictionary<string, List<CollectionTypeMapping>> mDatabases = new Dictionary<string, List<CollectionTypeMapping>>();

        public ConnectionDialog(ConnectionProperties props, bool isNewConnection, Func<string, Assembly> loadSafely)
        {
            this.mConnection = props;
            this.mIsNewConnection = isNewConnection;
            this.loadSafely = loadSafely;

            InitializeComponent();

            if (!this.mIsNewConnection)
                this.LoadFrom(props);
            else
                this.UpdateLoadedAssemblies();
        }

        public void LoadTypes()
        {
            if (string.IsNullOrWhiteSpace(this.txtConnectionString.Text))
                return;

            var connString = this.txtConnectionString.Text.Trim();

            var mongo = MongoServer.Create(connString);
            try
            {

                var typeLookup = this.LoadedTypes.ToLookup(x => x.Name);

                foreach (string name in mongo.GetDatabaseNames())
                {
                    List<CollectionTypeMapping> mappings = null;
                    if (!this.mDatabases.TryGetValue(name, out mappings))
                    {
                        mappings = new List<CollectionTypeMapping>();
                        this.mDatabases[name] = mappings;
                    }

                    var lookup = mappings.ToDictionary(x => x.CollectionName);

                    var db = mongo.GetDatabase(name);
                    foreach (string collectionName in db.GetCollectionNames())
                    {
                        CollectionTypeMapping mapping = null;
                        if (!lookup.TryGetValue(collectionName, out mapping))
                        {
                            mapping = new CollectionTypeMapping
                                          {
                                              CollectionName = collectionName
                                          };
                            mappings.Add(mapping);
                        }

                        if (mapping.CollectionType == null)
                        {
                            //see if we can't figure out the type for the collection
                            Type t = TryGetTypeForCollection(db, collectionName, typeLookup);
                            if (t != null)
                            {
                                mapping.CollectionType = t.ToString();
                            }

                        }
                    }
                }
            }
            finally
            {
                mongo.Disconnect();
            }

            this.cbDatabases.Items.Clear();
            this.cbDatabases.Items.AddRange(this.mDatabases.Keys.Cast<object>().ToArray());
            this.cbDatabases.SelectedItem = this.mDatabases.Keys.FirstOrDefault();
            if (this.cbDatabases.SelectedItem != null)
                this.dgCollectionTypes.DataSource = this.mDatabases[(string)this.cbDatabases.SelectedItem].Select(m => new TypeMappingWrapper(this, m)).ToList();
            else
                this.dgCollectionTypes.DataSource = null;
        }

        private Type TryGetTypeForCollection(MongoDatabase db, string collectionName, ILookup<string, Type> typeLookup)
        {
            
            var ret = db.Eval(string.Format("db['{0}'].findOne({{}}, {{'_t':1}})", collectionName));
            BsonElement typeElement;
            try
            {
                if (ret.AsBsonDocument.TryGetElement("_t", out typeElement))
                {
                    string type1 = typeElement.Value.AsBsonArray.First().AsString;
                    return typeLookup[type1].SingleOrDefault();
                }
            }catch(Exception ex)
            {
                //leave it as default
            }

            return default(Type);
        }

        #region event handlers
        private void ConnectionDialog_FormClosing(object sender, FormClosingEventArgs e)
        {
            if(!this.mCloseAlreadyValidated)
            {
                e.Cancel = !this.DoCancel();
                this.mCloseAlreadyValidated = false;
            }
        }

        private void btnSave_Click(object sender, EventArgs e)
        {
            if(this.DoSave())
            {
                this.mCloseAlreadyValidated = true;
                this.Close();
            }

            this.mCloseAlreadyValidated = false;
        }

        private void btnCancel_Click(object sender, EventArgs e)
        {
            if(this.DoCancel())
            {
                this.mCloseAlreadyValidated = true;
                this.Close();
            }

            this.mCloseAlreadyValidated = false;
        }

        private void btnAddAssembly_Click(object sender, EventArgs e)
        {
            using (var chooser = new OpenFileDialog())
            {
                chooser.ShowDialog();


                foreach (var ass in chooser.FileNames)
                {
                    var loaded = this.loadSafely(ass);
                    this.LoadedAssemblies.Add(loaded, ass);
                    this.LoadedTypes.AddRange(loaded.GetExportedTypes());
                }


                UpdateLoadedAssemblies();
            }
        }

        private void btnRemoveAssembly_Click(object sender, EventArgs e)
        {
            foreach (var ass in lbLoadedAssemblies.SelectedItems)
            {
                this.LoadedAssemblies.Remove(((KeyValuePair<Assembly, string>)ass).Key);
                var assTypes = new HashSet<Type>(((KeyValuePair<Assembly, string>)ass).Key.GetExportedTypes());
                this.LoadedTypes.RemoveWhere(assTypes.Contains);
            }

            UpdateLoadedAssemblies();
        }

        private void btnConnect_Click(object sender, EventArgs e)
        {
            this.LoadTypes();
        }

        private void tvKnownTypes_DoubleClick(object sender, EventArgs e)
        {
            if(tvKnownTypes.SelectedNode.Tag != null)
            {
                foreach (var row in this.dgCollectionTypes.SelectedCells.Cast<DataGridViewCell>().Where(x => string.Equals(x.OwningColumn.Name, "CollectionType")))
                {
                    row.Value = ((Type)tvKnownTypes.SelectedNode.Tag).ToString();
                }
            }
                
        }

        private void cbDatabases_SelectedValueChanged(object sender, EventArgs e)
        {
            if (this.cbDatabases.SelectedItem != null)
                this.dgCollectionTypes.DataSource = this.mDatabases[(string)this.cbDatabases.SelectedItem].Select(m => new TypeMappingWrapper(this, m)).ToList();
            else
                this.dgCollectionTypes.DataSource = null;
        }

        private void btnExport_Click(object sender, EventArgs e)
        {
            try
            {
                using (var chooser = new SaveFileDialog())
                {
                    var result = chooser.ShowDialog();

                    if (result == System.Windows.Forms.DialogResult.OK)
                    {
                        var doc = new XDocument(
                                new XDeclaration("1.0", "UTF-8", "yes")
                            );
                        var el = new XElement("Properties");
                        var working = new ConnectionProperties();
                        this.Populate(working);
                        working.Serialize(el);
                        doc.Add(el);

                        using (var writer = System.Xml.XmlWriter.Create(chooser.OpenFile(), new System.Xml.XmlWriterSettings
                                                                                                {
                                                                                                    Indent = true,
                                                                                                    CloseOutput = true
                                                                                                }))
                        {

                            doc.WriteTo(writer);
                            writer.Flush();
                        }
                    }
                }
            }catch(Exception ex)
            {
                MessageBox.Show("Export failed: " + ex);
            }
        }
        
        private void btnImport_Click(object sender, EventArgs e)
        {
            try
            {
                using (var chooser = new OpenFileDialog())
                {
                    var result = chooser.ShowDialog();

                    if (result == System.Windows.Forms.DialogResult.OK)
                    {
                        var props = new ConnectionProperties();
                        using (var reader = System.Xml.XmlReader.Create(chooser.OpenFile(), new System.Xml.XmlReaderSettings
                                                                                                {
                                                                                                    CloseInput = true
                                                                                                }))
                        {
                            var doc = XDocument.Load(reader);
                            props.Deserialize(doc.Element("Properties"));
                        }

                        this.LoadFrom(props);
                    }
                }
            }catch(Exception ex)
            {
                MessageBox.Show("Import failed: " + ex.ToString());
            }
        }
        #endregion

        private void Populate(ConnectionProperties props)
        {
            props.ConnectionString = this.txtConnectionString.Text.Trim();

            props.AssemblyLocations = new HashSet<string>();
            foreach (string loc in this.LoadedAssemblies.Values)
                props.AssemblyLocations.Add(loc);

            props.CollectionTypeMappings = new Dictionary<string, HashSet<CollectionTypeMapping>>();
            foreach(var pair in this.mDatabases)
            {
                var set = new HashSet<CollectionTypeMapping>();
                foreach(CollectionTypeMapping map in pair.Value)
                {
                    set.Add(map.Clone());
                }
                props.CollectionTypeMappings[pair.Key] = set;
            }

            props.SelectedDatabase = (string)this.cbDatabases.SelectedItem;

            props.CustomSerializers = new Dictionary<string, string>();
            foreach (var pair in this.SerializerMappings)
                props.CustomSerializers.Add(pair.Key.ToString(), pair.Value.ToString());
        }
        
        private void LoadFrom(ConnectionProperties props)
        {
            this.txtConnectionString.Text = props.ConnectionString;
            this.LoadedAssemblies = props.AssemblyLocations.ToDictionary(loc => this.loadSafely(loc));
            this.LoadedTypes.Clear();
            this.LoadedTypes.AddRange(MongoDynamicDataContextDriver.CommonTypes.Values);
            this.LoadedTypes.AddRange(this.LoadedAssemblies.Keys.SelectMany(ass => ass.GetExportedTypes()));
            this.mDatabases = new Dictionary<string, List<CollectionTypeMapping>>();
            foreach (var pair in props.CollectionTypeMappings)
            {
                this.mDatabases[pair.Key] = pair.Value.ToList();
            }

            this.cbDatabases.Items.Clear();
            this.cbDatabases.Items.AddRange(this.mDatabases.Keys.Cast<object>().ToArray());
            string db = this.mDatabases.Keys.FirstOrDefault();
            this.cbDatabases.SelectedItem = db;
            if (db != null)
                this.dgCollectionTypes.DataSource = this.mDatabases[db].Select(m => new TypeMappingWrapper(this, m)).ToList();
            else
                this.dgCollectionTypes.DataSource = null;
            if(props.SelectedDatabase != null)
                this.cbDatabases.SelectedIndex = this.cbDatabases.Items.IndexOf(props.SelectedDatabase);

            this.lbCustomSerializers.Items.Clear();
            this.SerializerMappings.Clear();
            if (props.CustomSerializers != null)
                foreach(var pair in props.CustomSerializers)
                {
                    var keyType = this.LoadedTypes.FirstOrDefault(x => string.Equals(pair.Key, x.FullName));
                    var valType = this.LoadedTypes.FirstOrDefault(x => string.Equals(pair.Value, x.FullName));
                    if (keyType != null && valType != null)
                        this.SerializerMappings[keyType] = valType;
                }
            this.lbCustomSerializers.Items.AddRange(this.SerializerMappings.Select(pair => new SerializerMappingWrapper { Type = pair.Key, Serializer = pair.Value }).Cast<object>().ToArray());

            UpdateLoadedAssemblies();
        }

        /// <summary>
        /// Checks to see if the data changed from save, and if so, prompts the user.
        /// </summary>
        /// <returns>True if the form should close, False if the form should stay open.</returns>
        private bool DoCancel()
        {
            var working = new ConnectionProperties();
            this.Populate(working);

            if(!working.Equals(this.mConnection))
            {
                var result = MessageBox.Show("Changes have been made.  Do you want to save first?", "Cancel", MessageBoxButtons.YesNoCancel);
                if(result == System.Windows.Forms.DialogResult.No)
                {
                    this.DialogResult = System.Windows.Forms.DialogResult.Cancel;
                    return true;
                }
                else if(result == System.Windows.Forms.DialogResult.Cancel)
                {
                    return false;
                }
                else if(result == System.Windows.Forms.DialogResult.Yes)
                {
                    return this.DoSave();
                }

                return false;
            }

            this.DialogResult = System.Windows.Forms.DialogResult.Cancel;
            return true;
        }

        /// <summary>
        /// validates and saves the data to the connection
        /// </summary>
        /// <returns>true if the data successfully saved, false if the form should stay open</returns>
        private bool DoSave()
        {
            var connStr = this.txtConnectionString.Text.Trim();

            this.Populate(this.mConnection);

            this.DialogResult = System.Windows.Forms.DialogResult.OK;
            return true;
        }

        
        private void UpdateLoadedAssemblies()
        {
            this.lbLoadedAssemblies.Items.Clear();
            this.lbLoadedAssemblies.Items.AddRange(this.LoadedAssemblies.Cast<object>().ToArray());

            this.tvKnownTypes.Nodes.Clear();
            this.tvKnownTypes.Nodes.AddRange(MakeTree(this.LoadedTypes).ToArray());
        }

        private IEnumerable<TreeNode> MakeTree(IEnumerable<Type> typeNames)
        {
            List<TreeNode> nodes = new List<TreeNode>();
            string[] currentPath = new string[0];
            TreeNode currentNode = null;
            foreach(Type t in typeNames)
            {
                if (t == null)
                    continue;

                string[] path = t.FullName.Split('.');

                if(path.Length <= 0) continue;

                while(currentNode != null &&
                    !currentPath.IsSamePath(path) &&
                    !currentPath.IsParentOf(path))
                {
                    currentNode = currentNode.Parent;
                    if (currentNode == null)
                        currentPath = new string[0];
                    else
                        currentPath = currentNode.Text.Split('.');
                }

                int startIndex = (currentNode == null ? 0 : currentPath.Length);

                for(int i = startIndex; i < path.Length; i++)
                {
                    TreeNode temp = new TreeNode();
                    string[] tempPath = new string[i + 1];
                    Array.Copy(path, tempPath, tempPath.Length);
                    temp.Text = string.Join(".", tempPath);

                    if(currentNode == null)
                    {
                        //top of tree
                        currentNode = temp;
                        currentPath = tempPath;
                        nodes.Add(temp);
                    }
                    else
                    {
                        currentNode.Nodes.Add(temp);
                        currentNode = temp;
                        currentPath = tempPath;
                    }
                }

                currentNode.Tag = t;
                currentNode.Text = t.FullName;
            }

            return nodes;
        }


        public class TypeMappingWrapper
        {
            private ConnectionDialog mDialog;
            private CollectionTypeMapping mData;

            public TypeMappingWrapper(ConnectionDialog dialog, CollectionTypeMapping data)
            {
                this.mDialog = dialog;
                this.mData = data;
            }


            public string CollectionName
            {
                get
                {
                    return mData.CollectionName;
                }
                set
                {
                    mData.CollectionName = value;
                }
            }

            
            public string CollectionType
            {
                get { return mData.CollectionType; }
                set { mData.CollectionType = value; }
                //get
                //{
                //    return mData.CollectionType;
                //}
                //set
                //{
                    
                //    var ass = mDialog.LoadedAssemblies.Keys.FirstOrDefault(x => x.GetType(value) != null);

                //    if (ass != null)
                //    {
                //        var type = ass.GetType(value);
                //        mData.CollectionType = type.AssemblyQualifiedName;
                //        mData.AssemblyName = ass.FullName;
                //    }
                //    else
                //    {
                //        var type = Type.GetType(value);
                //        if (type == null)
                //            throw new ArgumentException("Value must be a type");

                //        mData.CollectionType = type.AssemblyQualifiedName;
                //        mData.AssemblyName = null;
                //    }
                        
                //}
            }
        }

        public class SerializerMappingWrapper
        {
            public Type Type { get; set; }
            public Type Serializer { get; set; }

            public override string ToString()
            {
                return string.Format("{0} uses serializer {1}", Type, Serializer);
            }
        }

        private void btnAddSerializer_Click(object sender, EventArgs e)
        {
            var types = this.LoadedTypes.Where(x => typeof(IBsonSerializer).IsAssignableFrom(x));
            using (var frm = new TypeSelector(this.MakeTree(this.LoadedTypes).ToArray(), (this.MakeTree(types).ToArray())))
            {
                var result = frm.ShowDialog();
                if (result == System.Windows.Forms.DialogResult.OK)
                {
                    this.SerializerMappings[frm.SelectedType] = frm.SelectedSerializer;
                    this.lbCustomSerializers.Items.Clear();
                    this.lbCustomSerializers.Items.AddRange(
                        this.SerializerMappings.Select(pair => new SerializerMappingWrapper
                                                                   {
                                                                       Type = pair.Key,
                                                                       Serializer = pair.Value
                                                                   }).Cast<object>().ToArray());
                }
            }
        }

        private void btnRemoveSerializers_Click(object sender, EventArgs e)
        {
            if (this.lbCustomSerializers.SelectedItem == null)
                return;

            var item = (SerializerMappingWrapper)this.lbCustomSerializers.SelectedItem;
            this.SerializerMappings.Remove(item.Type);
            this.lbCustomSerializers.Items.Remove(item);
        }

        
    }
    
    

    public class CollectionTypeMapping
    {
        public string CollectionName { get; set; }

        public String CollectionType { get; set; }

        public bool Equals(CollectionTypeMapping other)
        {
            if (ReferenceEquals(null, other)) return false;
            if (ReferenceEquals(this, other)) return true;
            return Equals(other.CollectionName, CollectionName) &&
                Equals(other.CollectionType, this.CollectionType);
        }

        public override bool Equals(object obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            if (obj.GetType() != typeof (CollectionTypeMapping)) return false;
            return Equals((CollectionTypeMapping) obj);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return ((CollectionName != null ? CollectionName.GetHashCode() : 0)*397) ^
                    (this.CollectionType != null ? this.CollectionType.GetHashCode() : 0);
            }
        }

        public XElement Serialize()
        {
            var ret = new XElement("CollectionTypeMapping");
            ret.SetAttributeValue("CollectionName", this.CollectionName);
            ret.SetAttributeValue("CollectionType", this.CollectionType);
            //ret.SetAttributeValue("Assembly", this.AssemblyName);

            return ret;
        }

        public void Deserialize(XElement element)
        {
            if (!element.Name.Equals((XName)"CollectionTypeMapping"))
                throw new ArgumentException("Element must have name CollectionTypeMapping");

            var attr = element.Attribute("CollectionName");
            if (attr != null)
                this.CollectionName = attr.Value;

            attr = element.Attribute("CollectionType");
            if (attr != null)
                this.CollectionType = attr.Value;
            
            //attr = element.Attribute("Assembly");
            //if (attr != null)
            //    this.AssemblyName = attr.Value;
        }

        public CollectionTypeMapping Clone()
        {
            return new CollectionTypeMapping
                       {
                           CollectionName = this.CollectionName,
                           CollectionType = this.CollectionType
                       };
        }
    }

    //public class TypeToStringTypeConverter : TypeConverter
    //{
    //    public override bool CanConvertFrom(ITypeDescriptorContext context, Type sourceType)
    //    {
    //        return sourceType == typeof(string);
    //    }

    //    public override bool CanConvertTo(ITypeDescriptorContext context, Type destinationType)
    //    {
    //        return destinationType == typeof(string);
    //    }

    //    public override object ConvertFrom(ITypeDescriptorContext context, System.Globalization.CultureInfo culture, object value)
    //    {
    //        if (value == null)
    //            return default(Type);
    //        else
    //            return Type.GetType((string)value, false, true);
    //    }

    //    public override object ConvertTo(ITypeDescriptorContext context, System.Globalization.CultureInfo culture, object value, Type destinationType)
    //    {
    //        return ((Type)value).AssemblyQualifiedName;
    //    }
    //}

    class DynamicComparer<T> : IComparer<T>
    {
        public Func<T, T, int> Comparer { get; private set; }

        public DynamicComparer(Func<T, T, int> comparer)
        {
            this.Comparer = comparer;
        }

        public int Compare(T x, T y)
        {
            return Comparer(x, y);
        }
    }

    static class Extensions
    {
        public static SortedSet<T> AddRange<T>(this SortedSet<T> set, IEnumerable<T> values)
        {
            foreach (T val in values)
                set.Add(val);

            return set;
        }

        public static string NamespaceQualifiedName(this Type t)
        {
            return t.Namespace + "." + t.Name;
        }

        public static bool IsSamePath(this string[] path1, string[] path2)
        {
            if (path1.Length != path2.Length)
                return false;

            return path2.AsEnumerable().SequenceEqual(path1);
        }

        public static bool IsParentOf(this string[] me, string[] other)
        {
            //null cannot be parent or child
            if (me == null || other == null)
                return false;

            //child must be long enough to have content after the dot, i.e. 'parent.child'
            if (me.Length >= other.Length)
                return false;

            //the first 'me.length' topics must be equal
            return !me.Where((t, i) => !string.Equals(t, other[i], StringComparison.OrdinalIgnoreCase)).Any();
        }
    }
}