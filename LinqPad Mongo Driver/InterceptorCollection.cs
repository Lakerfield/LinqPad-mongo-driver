﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using System.Reflection;
using MongoDB.Driver.Builders;

namespace GDSX.Externals.LinqPad.Driver
{
    public class Interceptor<U> : MongoCollection<U>
    {
        /// <summary>
        /// If true, changes made to collection objects will be tracked and can be updated
        /// using SubmitChanges.  Defaults to off to avoid unnecessary serialization.
        /// </summary>
        public bool TrackChanges { get; set; }

        private Dictionary<object, U> mToUpdate = new Dictionary<object, U>();
        private Dictionary<object, int> mOriginalHashes = new Dictionary<object, int>();
        public IEnumerable<U> ToUpdate { get { return this.mToUpdate.Where(x => HasChanged(x.Value, mOriginalHashes[x.Key])).Select(x => x.Value); } }

        private Dictionary<object, U> mToDelete = new Dictionary<object, U>();
        public ICollection<U> ToDelete { get { return this.mToDelete.Values; } }

        private MongoCollection<U> mCollection;
        private TextWriter writer;
        public Interceptor(MongoCollection<U> coll, TextWriter writer)
            : base(coll.Database, (MongoCollectionSettings<U>)coll.Settings)
        {
            this.mCollection = coll;
            this.writer = writer;
            this.TrackChanges = false;
        }

        /// <summary>
        /// Submits all changes to queried objects by calling Save on the objects given by
        /// <see cref="ToUpdate"/>
        /// </summary>
        public int SubmitChanges()
        {
            int updated = 0;
            int deleted = 0;
            foreach (U item in this.ToUpdate)
            {
                var result = this.Save(item, SafeMode.True);
                if (result == null || result.Ok)
                    updated++;
                else
                    Console.WriteLine(string.Format("Unable to save Item \"{0}\" with ID {1}: {2}", item, GetId(item), result.ErrorMessage));
                
            }
            this.mToUpdate.Clear();

            foreach (var id in this.mToDelete.Keys)
            {
                if (id == null)
                {
                    Console.WriteLine(string.Format("Could not delete {0}, the object has no ObjectId or BsonIdAttribute", id));
                    continue;
                }
                
                var result = this.Remove(Query.EQ("_id", BsonValue.Create(id)));
                if (result == null || result.Ok)
                    deleted++;
                else
                    Console.WriteLine(string.Format("Unable to delete ID {0}: {1}", id, result.ErrorMessage));
            }
            this.mToDelete.Clear();

            if(updated == 0 && deleted == 0)
                return 0;

            StringBuilder sb = new StringBuilder("Collection \"");
            sb.Append(this.mCollection.Name).Append("\"");
            if(updated > 0){
                sb.AppendFormat(" updated {0} documents", updated);
                if (deleted > 0)
                    sb.Append(" and");
            }
            if (deleted > 0)
                sb.AppendFormat(" deleted {0} documents", deleted);

            Console.WriteLine(sb.ToString());

            return updated + deleted;
        }

        /// <summary>
        /// Deletes the given item when SubmitChanges() is called.
        /// </summary>
        /// <param name="item"></param>
        public void DeleteOnSubmit(U item)
        {
            var id = GetId(item);
            if (id == null)
                throw new Exception(string.Format("Cannot delete {0}, the object has not ObjectId or BsonIdAttribute", item));
            this.mToDelete[id] = item;
        }

        /// <summary>
        /// Checks the object against the original DeepHash to see if it changed.
        /// </summary>
        public static bool HasChanged<T>(T o, int originalHash)
        {
            return originalHash != DeepHash(o);
        }

        /// <summary>
        /// Serializes the object using the BsonSerializer, then hashes the
        /// serialized value.
        /// </summary>
        public static int DeepHash<T>(T o)
        {
            using (var stream = new MemoryStream())
            {
                using (var writer = BsonWriter.Create(stream))
                {
                    BsonSerializer.Serialize<T>(writer, o);
                    writer.Flush();
                    stream.Position = 0;
                    byte[] bytes = stream.GetBuffer();

                    int hash = 0;
                    unchecked
                    {
                        const int p = 16777619;
                        hash = (int)2166136261;

                        for (int i = 0; i < bytes.Length; i++)
                            hash = (hash ^ bytes[i]) * p;
                    }
                    return hash;
                }
            }
        }

        /// <summary>
        /// Gets the value of the ObjectId or BsonIdAttribute-marked identifier
        /// using reflection
        /// </summary>
        /// <param name="item"></param>
        /// <returns></returns>
        public static object GetId(object item)
        {
            var props = item.GetType().GetProperties();

            PropertyInfo id = item.GetType().GetProperty("Id", BindingFlags.Instance | BindingFlags.Public);
            if (id == null)
                id = props.FirstOrDefault(x => x.GetCustomAttributes(typeof(BsonIdAttribute), true).Any());

            if (id == null)
                return null;

            return id.GetValue(item, null);
        }

        private void TryRemember(object o)
        {
            if (!this.TrackChanges)
                return;

            if (o is U)
            {
                var id = GetId(o);
                if (id == null)
                    return;

                this.mToUpdate[id] = (U)o;
                this.mOriginalHashes[id] = DeepHash((U)o);
            }
        }

        #region overridden methods

        

        public override MongoCursor<T> FindAs<T>(IMongoQuery query)
        {
            if (writer != null)
            {
                this.writer.WriteLine("FindAs Query:");
                this.writer.WriteLine(query == null ? "No Query" : query.ToString());
                this.writer.WriteLine();
                this.writer.Flush();
            }
            if(typeof(U) == typeof(T))
                return new MongoCursorInterceptor<T>(mCollection.FindAs<T>(query),
                    this.TryRemember);
            else
                return base.FindAs<T>(query);
        }

        public override MongoCursor FindAs(Type documentType, IMongoQuery query)
        {
            if (writer != null)
            {
                this.writer.WriteLine("FindAs Query:");
                this.writer.WriteLine(query == null ? "No Query" : query.ToString());
                this.writer.WriteLine();
                this.writer.Flush();
            }
            return new MongoCursorInterceptor<U>(mCollection.FindAs<U>(query),
                    this.TryRemember);
        }

        public override FindAndModifyResult FindAndModify(
        IMongoQuery query,
        IMongoSortBy sortBy,
        IMongoUpdate update,
        bool returnNew,
        bool upsert)
        {
            if (writer != null)
            {
                this.writer.WriteLine("FindAndModify Query:");
                this.writer.WriteLine(query == null ? "No Query" : query.ToString());
                this.writer.WriteLine("SortBy: " + (sortBy == null ? "Nothing" : sortBy.ToString()));
                this.writer.WriteLine("Update: " + (update == null ? "Nothing" : update.ToString()));
                this.writer.WriteLine();
                this.writer.Flush();
            }
            return mCollection.FindAndModify(query, sortBy, update, returnNew, upsert);
        }


        public override FindAndModifyResult FindAndRemove(IMongoQuery query, IMongoSortBy sortBy)
        {
            if (writer != null)
            {
                this.writer.WriteLine("FindAndRemove Query:");
                this.writer.WriteLine(query == null ? "No Query" : query.ToString());
                this.writer.WriteLine("SortBy: " + (sortBy == null ? "Nothing" : sortBy.ToString()));
                this.writer.WriteLine();
                this.writer.Flush();
            }
            return mCollection.FindAndRemove(query, sortBy);
        }

        public override SafeModeResult Remove(IMongoQuery query, RemoveFlags flags, SafeMode safeMode)
        {
            if (writer != null)
            {
                this.writer.WriteLine("Remove Query:");
                this.writer.WriteLine(query == null ? "Null" : query.ToString());
                this.writer.WriteLine();
                this.writer.Flush();
            }

            return mCollection.Remove(query, flags, safeMode);
        }

        public override SafeModeResult Save(Type nominalType, object document, MongoInsertOptions options)
        {
            if (writer != null)
            {
                this.writer.WriteLine("Save:");
                this.writer.WriteLine("Type: " + (nominalType == null ? "Null" : nominalType.ToString()));
                if (document == null)
                    this.writer.WriteLine("Document: Null");
                else
                {
                    var id = GetId(document);
                    if (id == null)
                        this.writer.WriteLine("Document: " + document.ToString());
                    else
                        this.writer.WriteLine(string.Format("Document ID: \"{0}\"", id));
                }
                this.writer.WriteLine();
                this.writer.Flush();
            }

            return mCollection.Save(nominalType, document, options);
        }
        #endregion


        private class MongoCursorInterceptor<T> : MongoCursor<T>
        {
            private Action<object> TryRemember;
            public MongoCursorInterceptor(MongoCursor<T> cursor, Action<object> TryRemember) : base(cursor.Collection, cursor.Query)
            {
                this.TryRemember = TryRemember;
            }

            public override IEnumerator<T> GetEnumerator()
            {
                var e = base.GetEnumerator();
                return EnumerateRemaining(e).GetEnumerator();
                
            }

            protected override System.Collections.IEnumerator IEnumerableGetEnumerator()
            {
                var e = base.IEnumerableGetEnumerator();
                return EnumerateRemaining(e).GetEnumerator();
            }

            private IEnumerable<T> EnumerateRemaining(IEnumerator<T> e)
            {
                while (e.MoveNext())
                {
                    this.TryRemember(e.Current);
                    yield return e.Current;
                }
            }

            private IEnumerable EnumerateRemaining(IEnumerator e)
            {
                while (e.MoveNext())
                {
                    if (e.Current is T)
                        this.TryRemember(e.Current);
                    yield return e.Current;
                }
            }
        }
    }
}