#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;

namespace YuzeToolkit
{
    public static class LitJson
    {
        #region EvalToolHelpers

        public static object? Parse(string json) => ConvertJsonData(JsonMapper.ToObject(json));

        public static string Stringify(object? value) => JsonMapper.ToJson(value);

        private static object? ConvertJsonData(JsonData? data)
        {
            if (data == null) return null;

            if (data.IsObject)
            {
                var result = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var key in data.Keys)
                    result[key] = ConvertJsonData(data[key]);
                return result;
            }

            if (data.IsArray)
            {
                var result = new List<object?>(data.Count);
                for (var i = 0; i < data.Count; i++)
                    result.Add(ConvertJsonData(data[i]));
                return result;
            }

            var wrapper = (IJsonWrapper)data;
            if (data.IsString) return wrapper.GetString();
            if (data.IsBoolean) return wrapper.GetBoolean();
            if (data.IsInt) return wrapper.GetInt();
            if (data.IsLong) return wrapper.GetLong();
            if (data.IsDouble) return wrapper.GetDouble();
            return null;
        }

        #endregion

        #region IJsonWrapper.cs

        /// <summary>
        /// Describes the logical value currently stored inside a <see cref="JsonData"/> or
        /// <see cref="IJsonWrapper"/> instance.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <see cref="None"/> means the wrapper has not been initialized yet. This is not the
        /// same thing as a JSON <c>null</c> token.
        /// </para>
        /// <para>
        /// This LitJson variant does not model JSON <c>null</c> as a dedicated <see cref="JsonType"/>.
        /// Instead, JSON <c>null</c> is represented by a nullable wrapper reference, such as
        /// <see cref="JsonData"/> being <see langword="null"/>.
        /// </para>
        /// </remarks>
        public enum JsonType
        {
            None,

            Object,
            Array,
            String,
            Int,
            Long,
            Double,
            Boolean
        }

        /// <summary>
        /// Abstraction used by LitJson to represent a mutable JSON value.
        /// </summary>
        /// <remarks>
        /// <para>
        /// An implementation behaves like a tagged union: at any point it stores exactly one JSON
        /// shape described by <see cref="GetJsonType"/> and the corresponding <c>IsXxx</c> flags.
        /// </para>
        /// <para>
        /// Array and object wrappers also expose the non-generic collection interfaces so the parser
        /// can populate them without knowing the concrete implementation type.
        /// </para>
        /// <para>
        /// JSON <c>null</c> is not represented by a dedicated wrapper instance. Call sites should
        /// treat a nullable <see cref="IJsonWrapper"/> reference being <see langword="null"/> as
        /// the JSON null value.
        /// </para>
        /// </remarks>
        public interface IJsonWrapper : IList, IOrderedDictionary
        {
            bool IsArray { get; }
            bool IsBoolean { get; }
            bool IsDouble { get; }
            bool IsInt { get; }
            bool IsLong { get; }
            bool IsObject { get; }
            bool IsString { get; }

            bool GetBoolean();
            double GetDouble();
            int GetInt();
            JsonType GetJsonType();
            long GetLong();
            string GetString();

            void SetBoolean(bool val);
            void SetDouble(double val);
            void SetInt(int val);
            void SetJsonType(JsonType type);
            void SetLong(long val);
            void SetString(string val);

            string ToJson();
            void ToJson(JsonWriter writer);
        }

        #endregion

        #region JsonData.g.cs

        /// <summary>
        /// Default mutable JSON wrapper used by LitJson.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <see cref="JsonData"/> can represent any non-null JSON node: object, array, string,
        /// boolean, integer, long, or double.
        /// </para>
        /// <para>
        /// This implementation intentionally separates three states that are often confused:
        /// uninitialized data (<see cref="JsonType.None"/>), an initialized JSON value, and a JSON
        /// <c>null</c> token. JSON null is represented outside the instance by using a nullable
        /// <see cref="JsonData"/> reference.
        /// </para>
        /// <para>
        /// Object values preserve insertion order through an internal ordered key list, while also
        /// maintaining dictionary-style lookup.
        /// </para>
        /// </remarks>
        public class JsonData : IJsonWrapper, IEquatable<JsonData>
        {
            #region Fields

            private IList<JsonData?>? _instArray;
            private bool _instBoolean;
            private double _instDouble;
            private int _instINT;
            private long _instLong;
            private IDictionary<string, JsonData?>? _instObject;
            private string? _instString;
            private string? _json;
            private JsonType _type;

            // Used to implement the IOrderedDictionary interface
            private IList<KeyValuePair<string, JsonData?>>? _objectList;

            #endregion

            #region Properties

            public int Count => EnsureCollection().Count;

            public bool IsArray => _type == JsonType.Array;

            public bool IsBoolean => _type == JsonType.Boolean;

            public bool IsDouble => _type == JsonType.Double;

            public bool IsInt => _type == JsonType.Int;

            public bool IsLong => _type == JsonType.Long;

            public bool IsObject => _type == JsonType.Object;

            public bool IsString => _type == JsonType.String;

            public ICollection<string> Keys
            {
                get
                {
                    EnsureDictionary();
                    return _instObject!.Keys;
                }
            }

            /// <summary>
            /// Determines whether the json contains an element that has the specified key.
            /// </summary>
            /// <param name="key">The key to locate in the json.</param>
            /// <returns>true if the json contains an element that has the specified key; otherwise, false.</returns>
            /// <summary>
            /// Determines whether the current value is an object containing the specified property.
            /// </summary>
            /// <param name="key">Object property name to look up.</param>
            /// <returns><see langword="true"/> if the property exists; otherwise <see langword="false"/>.</returns>
            /// <exception cref="InvalidOperationException">
            /// Thrown when the current instance is not an object and cannot be treated as one.
            /// </exception>
            public bool ContainsKey(string key)
            {
                EnsureDictionary();
                return _instObject!.Keys.Contains(key);
            }

            #endregion


            #region ICollection Properties

            int ICollection.Count => Count;

            bool ICollection.IsSynchronized => EnsureCollection().IsSynchronized;

            object ICollection.SyncRoot => EnsureCollection().SyncRoot;

            #endregion


            #region IDictionary Properties

            bool IDictionary.IsFixedSize => EnsureDictionary().IsFixedSize;

            bool IDictionary.IsReadOnly => EnsureDictionary().IsReadOnly;

            ICollection IDictionary.Keys
            {
                get
                {
                    EnsureDictionary();
                    IList<string> keys = new List<string>();

                    foreach (var entry in
                             _objectList!)
                    {
                        keys.Add(entry.Key);
                    }

                    return (ICollection)keys;
                }
            }

            ICollection IDictionary.Values
            {
                get
                {
                    EnsureDictionary();
                    IList<JsonData?> values = new List<JsonData?>();

                    foreach (var entry in
                             _objectList!)
                    {
                        values.Add(entry.Value);
                    }

                    return (ICollection)values;
                }
            }

            #endregion


            #region IJsonWrapper Properties

            bool IJsonWrapper.IsArray => IsArray;

            bool IJsonWrapper.IsBoolean => IsBoolean;

            bool IJsonWrapper.IsDouble => IsDouble;

            bool IJsonWrapper.IsInt => IsInt;

            bool IJsonWrapper.IsLong => IsLong;

            bool IJsonWrapper.IsObject => IsObject;

            bool IJsonWrapper.IsString => IsString;

            #endregion


            #region IList Properties

            bool IList.IsFixedSize => EnsureList().IsFixedSize;

            bool IList.IsReadOnly => EnsureList().IsReadOnly;

            #endregion


            #region IDictionary Indexer

            object? IDictionary.this[object key]
            {
                get => EnsureDictionary()[key];

                set
                {
                    if (!(key is string s))
                        throw new ArgumentException(
                            "The key has to be a string");

                    var data = ToJsonData(value);

                    this[s] = data;
                }
            }

            #endregion


            #region IOrderedDictionary Indexer

            object? IOrderedDictionary.this[int idx]
            {
                get
                {
                    EnsureDictionary();
                    return _objectList![idx].Value;
                }

                set
                {
                    EnsureDictionary();
                    var data = ToJsonData(value);

                    var oldEntry = _objectList![idx];

                    _instObject![oldEntry.Key] = data;

                    var entry =
                        new KeyValuePair<string, JsonData?>(oldEntry.Key, data);

                    _objectList![idx] = entry;
                }
            }

            #endregion


            #region IList Indexer

            object? IList.this[int index]
            {
                get => EnsureList()[index];

                set
                {
                    EnsureList();
                    var data = ToJsonData(value);

                    this[index] = data;
                }
            }

            #endregion


            #region Public Indexers

            /// <summary>
            /// Gets or sets an object property by name.
            /// </summary>
            /// <remarks>
            /// <para>
            /// Reading or writing through this indexer forces the current instance into object mode
            /// when it is still uninitialized.
            /// </para>
            /// <para>
            /// A stored <see langword="null"/> value represents a JSON <c>null</c> property value.
            /// It is different from the property not existing at all.
            /// </para>
            /// </remarks>
            public JsonData? this[string propName]
            {
                get
                {
                    EnsureDictionary();
                    return _instObject![propName];
                }

                set
                {
                    EnsureDictionary();

                    var entry =
                        new KeyValuePair<string, JsonData?>(propName, value);

                    if (_instObject!.ContainsKey(propName))
                        for (var i = 0; i < _objectList!.Count; i++)
                        {
                            if (_objectList[i].Key == propName)
                            {
                                _objectList![i] = entry;
                                break;
                            }
                        }
                    else
                        _objectList!.Add(entry);

                    _instObject![propName] = value;

                    _json = null;
                }
            }

            /// <summary>
            /// Gets or sets an element by numeric index.
            /// </summary>
            /// <remarks>
            /// <para>
            /// When the current value is an array, the index targets the array element at that
            /// position.
            /// </para>
            /// <para>
            /// When the current value is an object, the index targets the property in insertion order.
            /// This mirrors LitJson's ordered-dictionary behavior.
            /// </para>
            /// </remarks>
            public JsonData? this[int index]
            {
                get
                {
                    EnsureCollection();

                    if (_type == JsonType.Array)
                        return _instArray![index];

                    return _objectList![index].Value;
                }

                set
                {
                    EnsureCollection();

                    if (_type == JsonType.Array)
                        _instArray![index] = value;
                    else
                    {
                        var entry = _objectList![index];
                        var newEntry =
                            new KeyValuePair<string, JsonData?>(entry.Key, value);

                        _objectList![index] = newEntry;
                        _instObject![entry.Key] = value;
                    }

                    _json = null;
                }
            }

            #endregion


            #region Constructors

            /// <summary>
            /// Creates an uninitialized wrapper.
            /// </summary>
            /// <remarks>
            /// The initial state is <see cref="JsonType.None"/>. The first strongly typed write,
            /// collection mutation, or explicit <see cref="SetJsonType"/> call determines the actual
            /// JSON shape stored by this instance.
            /// </remarks>
            public JsonData()
            {
            }

            public JsonData(bool boolean)
            {
                _type = JsonType.Boolean;
                _instBoolean = boolean;
            }

            public JsonData(double number)
            {
                _type = JsonType.Double;
                _instDouble = number;
            }

            public JsonData(int number)
            {
                _type = JsonType.Int;
                _instINT = number;
            }

            public JsonData(long number)
            {
                _type = JsonType.Long;
                _instLong = number;
            }

            /// <summary>
            /// Wraps a supported CLR primitive value as a JSON scalar node.
            /// </summary>
            /// <param name="obj">
            /// Supported values are <see cref="bool"/>, <see cref="double"/>, <see cref="int"/>,
            /// <see cref="long"/>, and <see cref="string"/>.
            /// </param>
            /// <exception cref="ArgumentNullException"><paramref name="obj"/> is <see langword="null"/>.</exception>
            /// <exception cref="ArgumentException">The runtime type of <paramref name="obj"/> is not supported.</exception>
            public JsonData(object obj)
            {
                if (obj == null)
                    throw new ArgumentNullException(nameof(obj));

                if (obj is bool b)
                {
                    _type = JsonType.Boolean;
                    _instBoolean = b;
                    return;
                }

                if (obj is double d)
                {
                    _type = JsonType.Double;
                    _instDouble = d;
                    return;
                }

                if (obj is int i)
                {
                    _type = JsonType.Int;
                    _instINT = i;
                    return;
                }

                if (obj is long l)
                {
                    _type = JsonType.Long;
                    _instLong = l;
                    return;
                }

                if (obj is string s)
                {
                    _type = JsonType.String;
                    _instString = s;
                    return;
                }

                throw new ArgumentException(
                    "Unable to wrap the given object with JsonData");
            }

            /// <summary>
            /// Creates a JSON string node.
            /// </summary>
            /// <param name="str">Non-null string content for the JSON string node.</param>
            /// <exception cref="ArgumentNullException"><paramref name="str"/> is <see langword="null"/>.</exception>
            public JsonData(string str)
            {
                _type = JsonType.String;
                _instString = str ?? throw new ArgumentNullException(nameof(str));
            }

            #endregion


            #region Implicit Conversions

            public static implicit operator JsonData(bool data)
            {
                return new JsonData(data);
            }

            public static implicit operator JsonData(double data)
            {
                return new JsonData(data);
            }

            public static implicit operator JsonData(int data)
            {
                return new JsonData(data);
            }

            public static implicit operator JsonData(long data)
            {
                return new JsonData(data);
            }

            public static implicit operator JsonData?(string? data)
            {
                return data == null ? null : new JsonData(data);
            }

            #endregion


            #region Explicit Conversions

            public static explicit operator bool(JsonData data)
            {
                if (data._type != JsonType.Boolean)
                    throw new InvalidCastException(
                        "Instance of JsonData doesn't hold a double");

                return data._instBoolean;
            }

            public static explicit operator double(JsonData data)
            {
                if (data._type != JsonType.Double)
                    throw new InvalidCastException(
                        "Instance of JsonData doesn't hold a double");

                return data._instDouble;
            }

            public static explicit operator int(JsonData data)
            {
                if (data._type != JsonType.Int && data._type != JsonType.Long)
                {
                    throw new InvalidCastException(
                        "Instance of JsonData doesn't hold an int");
                }

                // cast may truncate data... but that's up to the user to consider
                return data._type == JsonType.Int ? data._instINT : (int)data._instLong;
            }

            public static explicit operator long(JsonData data)
            {
                if (data._type != JsonType.Long && data._type != JsonType.Int)
                {
                    throw new InvalidCastException(
                        "Instance of JsonData doesn't hold a long");
                }

                return data._type == JsonType.Long ? data._instLong : data._instINT;
            }

            public static explicit operator string(JsonData data)
            {
                if (data._type != JsonType.String)
                    throw new InvalidCastException(
                        "Instance of JsonData doesn't hold a string");

                return data.EnsureStringValue();
            }

            #endregion


            #region ICollection Methods

            void ICollection.CopyTo(Array array, int index)
            {
                EnsureCollection().CopyTo(array, index);
            }

            #endregion


            #region IDictionary Methods

            void IDictionary.Add(object key, object? value)
            {
                var data = ToJsonData(value);

                EnsureDictionary().Add(key, data);

                var entry =
                    new KeyValuePair<string, JsonData?>((string)key, data);
                _objectList!.Add(entry);

                _json = null;
            }

            void IDictionary.Clear()
            {
                EnsureDictionary().Clear();
                _objectList!.Clear();
                _json = null;
            }

            bool IDictionary.Contains(object key)
            {
                return EnsureDictionary().Contains(key);
            }

            IDictionaryEnumerator IDictionary.GetEnumerator()
            {
                return ((IOrderedDictionary)this).GetEnumerator();
            }

            void IDictionary.Remove(object key)
            {
                EnsureDictionary().Remove(key);

                for (var i = 0; i < _objectList!.Count; i++)
                {
                    if (_objectList[i].Key == (string)key)
                    {
                        _objectList!.RemoveAt(i);
                        break;
                    }
                }

                _json = null;
            }

            #endregion


            #region IEnumerable Methods

            IEnumerator IEnumerable.GetEnumerator()
            {
                return EnsureCollection().GetEnumerator();
            }

            #endregion


            #region IJsonWrapper Methods

            bool IJsonWrapper.GetBoolean()
            {
                if (_type != JsonType.Boolean)
                    throw new InvalidOperationException(
                        "JsonData instance doesn't hold a boolean");

                return _instBoolean;
            }

            double IJsonWrapper.GetDouble()
            {
                if (_type != JsonType.Double)
                    throw new InvalidOperationException(
                        "JsonData instance doesn't hold a double");

                return _instDouble;
            }

            int IJsonWrapper.GetInt()
            {
                if (_type != JsonType.Int)
                    throw new InvalidOperationException(
                        "JsonData instance doesn't hold an int");

                return _instINT;
            }

            long IJsonWrapper.GetLong()
            {
                if (_type != JsonType.Long)
                    throw new InvalidOperationException(
                        "JsonData instance doesn't hold a long");

                return _instLong;
            }

            string IJsonWrapper.GetString()
            {
                if (_type != JsonType.String)
                    throw new InvalidOperationException(
                        "JsonData instance doesn't hold a string");

                return EnsureStringValue();
            }

            void IJsonWrapper.SetBoolean(bool val)
            {
                _type = JsonType.Boolean;
                _instBoolean = val;
                _json = null;
            }

            void IJsonWrapper.SetDouble(double val)
            {
                _type = JsonType.Double;
                _instDouble = val;
                _json = null;
            }

            void IJsonWrapper.SetInt(int val)
            {
                _type = JsonType.Int;
                _instINT = val;
                _json = null;
            }

            void IJsonWrapper.SetLong(long val)
            {
                _type = JsonType.Long;
                _instLong = val;
                _json = null;
            }

            void IJsonWrapper.SetString(string val)
            {
                _type = JsonType.String;
                _instString = val ?? throw new ArgumentNullException(nameof(val));
                _json = null;
            }

            string IJsonWrapper.ToJson()
            {
                return ToJson();
            }

            void IJsonWrapper.ToJson(JsonWriter writer)
            {
                ToJson(writer);
            }

            #endregion


            #region IList Methods

            int IList.Add(object? value)
            {
                return Add(value);
            }

            void IList.Clear()
            {
                EnsureList().Clear();
                _json = null;
            }

            bool IList.Contains(object? value)
            {
                return EnsureList().Contains(value);
            }

            int IList.IndexOf(object? value)
            {
                return EnsureList().IndexOf(value);
            }

            void IList.Insert(int index, object? value)
            {
                EnsureList().Insert(index, value);
                _json = null;
            }

            void IList.Remove(object? value)
            {
                EnsureList().Remove(value);
                _json = null;
            }

            void IList.RemoveAt(int index)
            {
                EnsureList().RemoveAt(index);
                _json = null;
            }

            #endregion


            #region IOrderedDictionary Methods

            IDictionaryEnumerator IOrderedDictionary.GetEnumerator()
            {
                EnsureDictionary();

                return new OrderedDictionaryEnumerator(
                    _objectList!.GetEnumerator());
            }

            void IOrderedDictionary.Insert(int idx, object key, object? value)
            {
                var property = (string)key;
                var data = ToJsonData(value);

                this[property] = data;

                var entry =
                    new KeyValuePair<string, JsonData?>(property, data);

                _objectList!.Insert(idx, entry);
            }

            void IOrderedDictionary.RemoveAt(int idx)
            {
                EnsureDictionary();

                _instObject!.Remove(_objectList![idx].Key);
                _objectList!.RemoveAt(idx);
            }

            #endregion


            #region Private Methods

            private ICollection EnsureCollection() => _type switch
            {
                JsonType.Array => (ICollection)_instArray!,
                JsonType.Object => (ICollection)_instObject!,
                _ => throw new InvalidOperationException("The JsonData instance has to be initialized first")
            };

            private IDictionary EnsureDictionary()
            {
                if (_type == JsonType.Object)
                    return (IDictionary)_instObject!;

                if (_type != JsonType.None)
                    throw new InvalidOperationException(
                        "Instance of JsonData is not a dictionary");

                _type = JsonType.Object;
                _instObject = new Dictionary<string, JsonData?>();
                _objectList = new List<KeyValuePair<string, JsonData?>>();

                return (IDictionary)_instObject;
            }

            private IList EnsureList()
            {
                if (_type == JsonType.Array)
                    return (IList)_instArray!;

                if (_type != JsonType.None)
                    throw new InvalidOperationException(
                        "Instance of JsonData is not a list");

                _type = JsonType.Array;
                _instArray = new List<JsonData?>();

                return (IList)_instArray;
            }

            private string EnsureStringValue()
            {
                if (_type != JsonType.String)
                    throw new InvalidOperationException(
                        "Instance of JsonData doesn't hold a string");

                return _instString ?? throw new InvalidOperationException(
                    "JsonData string instances must contain a non-null string value");
            }

            private JsonData? ToJsonData(object? obj) => obj switch
            {
                null => null,
                JsonData data => data,
                _ => new JsonData(obj)
            };

            private static void WriteJson(IJsonWrapper? obj, JsonWriter writer)
            {
                if (obj == null)
                {
                    writer.Write(null);
                    return;
                }

                if (obj.IsString)
                {
                    writer.Write(obj.GetString());
                    return;
                }

                if (obj.IsBoolean)
                {
                    writer.Write(obj.GetBoolean());
                    return;
                }

                if (obj.IsDouble)
                {
                    writer.Write(obj.GetDouble());
                    return;
                }

                if (obj.IsInt)
                {
                    writer.Write(obj.GetInt());
                    return;
                }

                if (obj.IsLong)
                {
                    writer.Write(obj.GetLong());
                    return;
                }

                if (obj.IsArray)
                {
                    writer.WriteArrayStart();
                    foreach (var elem in (IList)obj)
                        WriteJson((JsonData?)elem, writer);
                    writer.WriteArrayEnd();

                    return;
                }

                if (obj.IsObject)
                {
                    writer.WriteObjectStart();

                    foreach (DictionaryEntry entry in ((IDictionary)obj))
                    {
                        writer.WritePropertyName((string)entry.Key);
                        WriteJson((JsonData?)entry.Value, writer);
                    }

                    writer.WriteObjectEnd();
                }
            }

            #endregion


            /// <summary>
            /// Appends a value to the current array.
            /// </summary>
            /// <param name="value">
            /// A supported primitive, another <see cref="JsonData"/> instance, or
            /// <see langword="null"/> to append a JSON <c>null</c> element.
            /// </param>
            /// <returns>The zero-based index at which the item was inserted.</returns>
            /// <remarks>
            /// If the current instance is still uninitialized, this call materializes it as a JSON
            /// array.
            /// </remarks>
            public int Add(object? value)
            {
                var data = ToJsonData(value);

                _json = null;

                return EnsureList().Add(data);
            }

            /// <summary>
            /// Removes an item from the current object or array.
            /// </summary>
            /// <param name="obj">
            /// For objects, this must be the property name to remove. For arrays, this is the value to
            /// remove, using LitJson's wrapper conversion rules.
            /// </param>
            /// <returns><see langword="true"/> if an item was removed; otherwise <see langword="false"/>.</returns>
            /// <exception cref="InvalidOperationException">
            /// Thrown when the current value is neither an object nor an array.
            /// </exception>
            public bool Remove(object? obj)
            {
                _json = null;
                if (IsObject)
                {
                    if (obj is not string key)
                        throw new ArgumentException("The key has to be a string", nameof(obj));

                    if (_instObject!.TryGetValue(key, out var value))
                        return _instObject.Remove(key) &&
                               _objectList!.Remove(new KeyValuePair<string, JsonData?>(key, value));
                    throw new KeyNotFoundException("The specified key was not found in the JsonData object.");
                }

                if (IsArray)
                {
                    return _instArray!.Remove(ToJsonData(obj));
                }

                throw new InvalidOperationException(
                    "Instance of JsonData is not an object or a list.");
            }

            /// <summary>
            /// Removes all members from the current object or array.
            /// </summary>
            /// <remarks>
            /// Scalars are left unchanged because there is no collection content to clear.
            /// </remarks>
            public void Clear()
            {
                if (IsObject)
                {
                    ((IDictionary)this).Clear();
                    return;
                }

                if (IsArray)
                {
                    ((IList)this).Clear();
                }
            }

            public bool Equals(JsonData? x)
            {
                if (x == null)
                    return false;

                if (x._type != _type)
                {
                    // further check to see if this is a long to int comparison
                    if ((x._type != JsonType.Int && x._type != JsonType.Long)
                        || (_type != JsonType.Int && _type != JsonType.Long))
                    {
                        return false;
                    }
                }

                switch (_type)
                {
                    case JsonType.None:
                        return true;

                    case JsonType.Object:
                        return Equals(_instObject, x._instObject);

                    case JsonType.Array:
                        return Equals(_instArray, x._instArray);

                    case JsonType.String:
                        return string.Equals(_instString, x._instString, StringComparison.Ordinal);

                    case JsonType.Int:
                    {
                        if (x.IsLong)
                        {
                            if (x._instLong is < int.MinValue or > int.MaxValue)
                                return false;
                            return _instINT.Equals((int)x._instLong);
                        }

                        return _instINT.Equals(x._instINT);
                    }

                    case JsonType.Long:
                    {
                        if (x.IsInt)
                        {
                            if (_instLong is < int.MinValue or > int.MaxValue)
                                return false;
                            return x._instINT.Equals((int)_instLong);
                        }

                        return _instLong.Equals(x._instLong);
                    }

                    case JsonType.Double:
                        return _instDouble.Equals(x._instDouble);

                    case JsonType.Boolean:
                        return _instBoolean.Equals(x._instBoolean);
                }

                return false;
            }

            /// <summary>
            /// Returns the current logical JSON type stored by this instance.
            /// </summary>
            public JsonType GetJsonType()
            {
                return _type;
            }

            /// <summary>
            /// Reinitializes the current instance to hold the specified JSON shape.
            /// </summary>
            /// <param name="type">Target JSON shape.</param>
            /// <remarks>
            /// <para>
            /// Changing the type discards any previously stored content and creates fresh backing
            /// storage appropriate for the new shape.
            /// </para>
            /// <para>
            /// <see cref="JsonType.None"/> resets the instance back to the uninitialized state.
            /// </para>
            /// </remarks>
            public void SetJsonType(JsonType type)
            {
                if (_type == type)
                    return;

                switch (type)
                {
                    case JsonType.None:
                        break;

                    case JsonType.Object:
                        _instObject = new Dictionary<string, JsonData?>();
                        _objectList = new List<KeyValuePair<string, JsonData?>>();
                        break;

                    case JsonType.Array:
                        _instArray = new List<JsonData?>();
                        break;

                    case JsonType.String:
                        _instString = string.Empty;
                        break;

                    case JsonType.Int:
                        _instINT = 0;
                        break;

                    case JsonType.Long:
                        _instLong = 0;
                        break;

                    case JsonType.Double:
                        _instDouble = 0;
                        break;

                    case JsonType.Boolean:
                        _instBoolean = false;
                        break;
                }

                _type = type;
            }

            /// <summary>
            /// Serializes the current value to a compact JSON string.
            /// </summary>
            /// <returns>Compact JSON text representing this instance.</returns>
            /// <remarks>
            /// The generated string is cached until the instance is mutated again.
            /// </remarks>
            public string ToJson()
            {
                if (_json != null)
                    return _json;

                var sw = new StringWriter();
                var writer = new JsonWriter(sw)
                {
                    Validate = false
                };

                WriteJson(this, writer);
                _json = sw.ToString();

                return _json;
            }

            /// <summary>
            /// Writes the current value into an existing <see cref="JsonWriter"/>.
            /// </summary>
            /// <param name="writer">Destination writer.</param>
            /// <remarks>
            /// Validation is temporarily disabled while this wrapper writes itself because the wrapper
            /// already knows its own structural correctness.
            /// </remarks>
            public void ToJson(JsonWriter writer)
            {
                var oldValidate = writer.Validate;

                writer.Validate = false;

                WriteJson(this, writer);

                writer.Validate = oldValidate;
            }

            public override string ToString() => _type switch
            {
                JsonType.Array => "JsonData array",
                JsonType.Boolean => _instBoolean.ToString(),
                JsonType.Double => _instDouble.ToString(),
                JsonType.Int => _instINT.ToString(),
                JsonType.Long => _instLong.ToString(),
                JsonType.Object => "JsonData object",
                JsonType.String => EnsureStringValue(),
                _ => "Uninitialized JsonData"
            };
        }


        internal class OrderedDictionaryEnumerator : IDictionaryEnumerator
        {
            private readonly IEnumerator<KeyValuePair<string, JsonData?>> _listEnumerator;


            public object Current => Entry;

            public DictionaryEntry Entry
            {
                get
                {
                    var curr = _listEnumerator.Current;
                    return new DictionaryEntry(curr.Key, curr.Value);
                }
            }

            public object Key => _listEnumerator.Current.Key;

            public object? Value => _listEnumerator.Current.Value;


            public OrderedDictionaryEnumerator(
                IEnumerator<KeyValuePair<string, JsonData?>> enumerator)
            {
                _listEnumerator = enumerator;
            }


            public bool MoveNext()
            {
                return _listEnumerator.MoveNext();
            }

            public void Reset()
            {
                _listEnumerator.Reset();
            }
        }

        #endregion

        #region JsonException.cs

        public class JsonException :
#if NETSTANDARD1_5
        Exception
#else
            ApplicationException
#endif
        {
            public JsonException()
            {
            }

            internal JsonException(ParserToken token) :
                base($"Invalid token '{token}' in input string")
            {
            }

            internal JsonException(ParserToken token,
                Exception innerException) :
                base($"Invalid token '{token}' in input string",
                    innerException)
            {
            }

            internal JsonException(int c) :
                base($"Invalid character '{(char)c}' in input string")
            {
            }

            internal JsonException(int c, Exception innerException) :
                base($"Invalid character '{(char)c}' in input string",
                    innerException)
            {
            }


            public JsonException(string message) : base(message)
            {
            }

            public JsonException(string message, Exception innerException) :
                base(message, innerException)
            {
            }
        }

        #endregion

        #region JsonMapper.g.cs

        internal struct PropertyMetadata
        {
            public MemberInfo Info;
            public bool IsField;
            public Type Type;
        }


        internal struct ArrayMetadata
        {
            private Type _elementType;
            private bool _isArray;
            private bool _isList;


            public Type ElementType
            {
                get
                {
                    if (_elementType == null)
                        return typeof(JsonData);

                    return _elementType;
                }

                set => _elementType = value;
            }

            public bool IsArray
            {
                get => _isArray;
                set => _isArray = value;
            }

            public bool IsList
            {
                get => _isList;
                set => _isList = value;
            }
        }


        internal struct ObjectMetadata
        {
            private Type _elementType;
            private bool _isDictionary;

            private IDictionary<string, PropertyMetadata>? _properties;


            public Type ElementType
            {
                get
                {
                    if (_elementType == null)
                        return typeof(JsonData);

                    return _elementType;
                }

                set => _elementType = value;
            }

            public bool IsDictionary
            {
                get => _isDictionary;
                set => _isDictionary = value;
            }

            public IDictionary<string, PropertyMetadata> Properties
            {
                get => _properties ??
                       throw new InvalidOperationException("Object metadata properties were not initialized");
                set => _properties = value;
            }
        }


        internal delegate void ExporterFunc(object obj, JsonWriter writer);

        public delegate void ExporterFunc<T>(T obj, JsonWriter writer);

        internal delegate object? ImporterFunc(object input);

        public delegate TValue ImporterFunc<TJson, TValue>(TJson input);

        public delegate IJsonWrapper WrapperFactory();


        /// <summary>
        /// Main high-level entry point for LitJson serialization and deserialization.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <see cref="JsonMapper"/> performs reflection-based mapping between CLR objects and JSON,
        /// while also supporting the lower-level wrapper model used by <see cref="JsonData"/>.
        /// </para>
        /// <para>
        /// Importers and exporters can be registered to customize conversions between specific JSON
        /// token types and target CLR types.
        /// </para>
        /// </remarks>
        public class JsonMapper
        {
            #region Fields

            private static readonly int MaxNestingDepth;

            private static readonly IFormatProvider DatetimeFormat;

            private static readonly IDictionary<Type, ExporterFunc> BaseExportersTable;
            private static readonly IDictionary<Type, ExporterFunc> CustomExportersTable;
            private static readonly object CustomExportersTableLock = new();

            private static readonly IDictionary<Type,
                IDictionary<Type, ImporterFunc>> BaseImportersTable;

            private static readonly IDictionary<Type,
                IDictionary<Type, ImporterFunc>> CustomImportersTable;

            private static readonly object CustomImportersTableLock = new();

            private static readonly IDictionary<Type, ArrayMetadata> ArrayMetadata;
            private static readonly object ArrayMetadataLock = new();

            private static readonly IDictionary<Type,
                IDictionary<Type, MethodInfo?>> ConvOps;

            private static readonly object ConvOpsLock = new();

            private static readonly IDictionary<Type, ObjectMetadata> ObjectMetadata;
            private static readonly object ObjectMetadataLock = new();

            private static readonly IDictionary<Type,
                IList<PropertyMetadata>> TypeProperties;

            private static readonly object TypePropertiesLock = new();

            private static readonly JsonWriter StaticWriter;
            private static readonly object StaticWriterLock = new();

            #endregion


            #region Constructors

            static JsonMapper()
            {
                MaxNestingDepth = 100;

                ArrayMetadata = new Dictionary<Type, ArrayMetadata>();
                ConvOps = new Dictionary<Type, IDictionary<Type, MethodInfo?>>();
                ObjectMetadata = new Dictionary<Type, ObjectMetadata>();
                TypeProperties = new Dictionary<Type,
                    IList<PropertyMetadata>>();

                StaticWriter = new JsonWriter();

                DatetimeFormat = DateTimeFormatInfo.InvariantInfo;

                BaseExportersTable = new Dictionary<Type, ExporterFunc>();
                CustomExportersTable = new Dictionary<Type, ExporterFunc>();

                BaseImportersTable = new Dictionary<Type,
                    IDictionary<Type, ImporterFunc>>();
                CustomImportersTable = new Dictionary<Type,
                    IDictionary<Type, ImporterFunc>>();

                RegisterBaseExporters();
                RegisterBaseImporters();
            }

            #endregion


            #region Private Methods

            private static void AddArrayMetadata(Type type)
            {
                if (ArrayMetadata.ContainsKey(type))
                    return;

                var data = new ArrayMetadata
                {
                    IsArray = type.IsArray
                };

                if (type.GetInterface("System.Collections.IList") != null)
                    data.IsList = true;

                foreach (var pInfo in type.GetProperties())
                {
                    if (pInfo.Name != "Item")
                        continue;

                    ParameterInfo[] parameters = pInfo.GetIndexParameters();

                    if (parameters.Length != 1)
                        continue;

                    if (parameters[0].ParameterType == typeof(int))
                        data.ElementType = pInfo.PropertyType;
                }

                lock (ArrayMetadataLock)
                {
                    try
                    {
                        ArrayMetadata.Add(type, data);
                    }
                    catch (ArgumentException)
                    {
                    }
                }
            }

            private static void AddObjectMetadata(Type type)
            {
                if (ObjectMetadata.ContainsKey(type))
                    return;

                var data = new ObjectMetadata();

                if (type.GetInterface("System.Collections.IDictionary") != null)
                    data.IsDictionary = true;

                data.Properties = new Dictionary<string, PropertyMetadata>();

                foreach (var pInfo in type.GetProperties())
                {
                    if (pInfo.Name == "Item")
                    {
                        ParameterInfo[] parameters = pInfo.GetIndexParameters();

                        if (parameters.Length != 1)
                            continue;

                        if (parameters[0].ParameterType == typeof(string))
                            data.ElementType = pInfo.PropertyType;

                        continue;
                    }

                    var pData = new PropertyMetadata
                    {
                        Info = pInfo,
                        Type = pInfo.PropertyType
                    };

                    data.Properties.Add(pInfo.Name, pData);
                }

                foreach (var fInfo in type.GetFields())
                {
                    var pData = new PropertyMetadata
                    {
                        Info = fInfo,
                        IsField = true,
                        Type = fInfo.FieldType
                    };

                    data.Properties.Add(fInfo.Name, pData);
                }

                lock (ObjectMetadataLock)
                {
                    try
                    {
                        ObjectMetadata.Add(type, data);
                    }
                    catch (ArgumentException)
                    {
                    }
                }
            }

            private static void AddTypeProperties(Type type)
            {
                if (TypeProperties.ContainsKey(type))
                    return;

                IList<PropertyMetadata> props = new List<PropertyMetadata>();

                foreach (var pInfo in type.GetProperties())
                {
                    if (pInfo.Name == "Item")
                        continue;

                    var pData = new PropertyMetadata
                    {
                        Info = pInfo,
                        IsField = false
                    };
                    props.Add(pData);
                }

                foreach (var fInfo in type.GetFields())
                {
                    var pData = new PropertyMetadata
                    {
                        Info = fInfo,
                        IsField = true
                    };

                    props.Add(pData);
                }

                lock (TypePropertiesLock)
                {
                    try
                    {
                        TypeProperties.Add(type, props);
                    }
                    catch (ArgumentException)
                    {
                    }
                }
            }

            private static MethodInfo? GetConvOp(Type t1, Type t2)
            {
                lock (ConvOpsLock)
                {
                    if (!ConvOps.ContainsKey(t1))
                        ConvOps.Add(t1, new Dictionary<Type, MethodInfo?>());

                    if (ConvOps[t1].ContainsKey(t2))
                        return ConvOps[t1][t2];

                    var op = t1.GetMethod(
                        "op_Implicit", new[] { t2 });

                    try
                    {
                        ConvOps[t1].Add(t2, op);
                    }
                    catch (ArgumentException)
                    {
                        return ConvOps[t1][t2];
                    }

                    return op;
                }
            }

            private static object? ReadValue(Type instType, JsonReader reader)
            {
                reader.Read();

                if (reader.Token == JsonToken.ArrayEnd)
                    return null;

                var underlyingType = Nullable.GetUnderlyingType(instType);
                var valueType = underlyingType ?? instType;

                if (reader.Token == JsonToken.Null)
                {
#if NETSTANDARD1_5
                if (instType.IsClass() || underlyingType != null) {
                    return null;
                }
#else
                    if (instType.IsClass || underlyingType != null)
                    {
                        return null;
                    }
#endif

                    throw new JsonException($"Can't assign null to an instance of type {instType}");
                }

                if (reader.Token is JsonToken.Double or JsonToken.Int or JsonToken.Long or JsonToken.String
                    or JsonToken.Boolean)
                {
                    var readerValue =
                        reader.Value ?? throw new JsonException("Primitive JSON token didn't provide a value");
                    var jsonType = readerValue.GetType();

                    if (valueType.IsAssignableFrom(jsonType))
                        return readerValue;

                    // If there's a custom importer that fits, use it
                    lock (CustomImportersTableLock)
                    {
                        if (CustomImportersTable.TryGetValue(jsonType,
                                out var customImporterTablesValue) &&
                            customImporterTablesValue.TryGetValue(valueType, out var customImporter))
                        {
                            return customImporter(readerValue);
                        }
                    }

                    // Maybe there's a base importer that works
                    if (BaseImportersTable.TryGetValue(jsonType,
                            out var baseImporterTablesValue) &&
                        baseImporterTablesValue.TryGetValue(valueType, out var baseImporter))
                    {
                        return baseImporter(readerValue);
                    }

                    // Maybe it's an enum
#if NETSTANDARD1_5
                if (value_type.IsEnum())
                    return Enum.ToObject(valueType, readerValue);
#else
                    if (valueType.IsEnum)
                        return Enum.ToObject(valueType, readerValue);
#endif
                    // Try using an implicit conversion operator
                    var convOp = GetConvOp(valueType, jsonType);

                    if (convOp != null)
                        return convOp.Invoke(null,
                            new[] { readerValue });

                    // No luck
                    throw new JsonException(string.Format(
                        "Can't assign value '{0}' (type {1}) to type {2}",
                        readerValue, jsonType, instType));
                }

                object? instance = null;

                if (reader.Token == JsonToken.ArrayStart)
                {
                    AddArrayMetadata(instType);
                    var tData = ArrayMetadata[instType];

                    if (!tData.IsArray && !tData.IsList)
                        throw new JsonException($"Type {instType} can't act as an array");

                    IList list;
                    Type elemType;

                    if (!tData.IsArray)
                    {
                        list = (IList)(Activator.CreateInstance(instType)
                                       ?? throw new JsonException($"Can't instantiate type {instType}"));
                        elemType = tData.ElementType;
                    }
                    else
                    {
                        list = new ArrayList();
                        elemType = instType.GetElementType()
                                   ?? throw new JsonException($"Array type {instType} doesn't expose an element type");
                    }

                    list.Clear();

                    while (true)
                    {
                        var item = ReadValue(elemType, reader);
                        if (item == null && reader.Token == JsonToken.ArrayEnd)
                            break;

                        list.Add(item);
                    }

                    if (tData.IsArray)
                    {
                        var n = list.Count;
                        instance = Array.CreateInstance(elemType, n);

                        for (var i = 0; i < n; i++)
                            ((Array)instance).SetValue(list[i], i);
                    }
                    else
                        instance = list;
                }
                else if (reader.Token == JsonToken.ObjectStart)
                {
                    AddObjectMetadata(valueType);
                    var tData = ObjectMetadata[valueType];

                    instance = Activator.CreateInstance(valueType)
                               ?? throw new JsonException($"Can't instantiate type {valueType}");

                    while (true)
                    {
                        reader.Read();

                        if (reader.Token == JsonToken.ObjectEnd)
                            break;

                        var property = reader.Value as string
                                       ?? throw new JsonException("Expected an object property name");

                        if (tData.Properties.ContainsKey(property))
                        {
                            var propData =
                                tData.Properties[property];

                            if (propData.IsField)
                            {
                                ((FieldInfo)propData.Info).SetValue(
                                    instance, ReadValue(propData.Type, reader));
                            }
                            else
                            {
                                var pInfo =
                                    (PropertyInfo)propData.Info;

                                if (pInfo.CanWrite)
                                    pInfo.SetValue(
                                        instance,
                                        ReadValue(propData.Type, reader),
                                        null);
                                else
                                    ReadValue(propData.Type, reader);
                            }
                        }
                        else
                        {
                            if (!tData.IsDictionary)
                            {
                                if (!reader.SkipNonMembers)
                                {
                                    throw new JsonException(string.Format(
                                        "The type {0} doesn't have the " +
                                        "property '{1}'",
                                        instType, property));
                                }

                                ReadSkip(reader);
                                continue;
                            }

                            ((IDictionary)instance).Add(
                                property, ReadValue(
                                    tData.ElementType, reader));
                        }
                    }
                }

                return instance;
            }

            private static IJsonWrapper? ReadValue(WrapperFactory factory,
                JsonReader reader)
            {
                reader.Read();

                if (reader.Token is JsonToken.ArrayEnd or JsonToken.Null)
                    return null;

                var instance = factory();

                if (reader.Token == JsonToken.String)
                {
                    instance.SetString((string)(reader.Value ?? throw new JsonException("Expected a string value")));
                    return instance;
                }

                if (reader.Token == JsonToken.Double)
                {
                    instance.SetDouble((double)(reader.Value ?? throw new JsonException("Expected a double value")));
                    return instance;
                }

                if (reader.Token == JsonToken.Int)
                {
                    instance.SetInt((int)(reader.Value ?? throw new JsonException("Expected an int value")));
                    return instance;
                }

                if (reader.Token == JsonToken.Long)
                {
                    instance.SetLong((long)(reader.Value ?? throw new JsonException("Expected a long value")));
                    return instance;
                }

                if (reader.Token == JsonToken.Boolean)
                {
                    instance.SetBoolean((bool)(reader.Value ?? throw new JsonException("Expected a boolean value")));
                    return instance;
                }

                if (reader.Token == JsonToken.ArrayStart)
                {
                    instance.SetJsonType(JsonType.Array);

                    while (true)
                    {
                        var item = ReadValue(factory, reader);
                        if (item == null && reader.Token == JsonToken.ArrayEnd)
                            break;

                        instance.Add(item);
                    }
                }
                else if (reader.Token == JsonToken.ObjectStart)
                {
                    instance.SetJsonType(JsonType.Object);

                    while (true)
                    {
                        reader.Read();

                        if (reader.Token == JsonToken.ObjectEnd)
                            break;

                        var property = reader.Value as string
                                       ?? throw new JsonException("Expected an object property name");

                        instance[property] = ReadValue(
                            factory, reader);
                    }
                }

                return instance;
            }

            private static void ReadSkip(JsonReader reader)
            {
                ToWrapper(() => new JsonMockWrapper(), reader);
            }

            private static void RegisterBaseExporters()
            {
                // This method is only called from the static initializer,
                // so there is no need to explicitly lock any static members here
                BaseExportersTable[typeof(byte)] =
                    delegate(object obj, JsonWriter writer) { writer.Write(Convert.ToInt32((byte)obj)); };

                BaseExportersTable[typeof(char)] =
                    delegate(object obj, JsonWriter writer) { writer.Write(Convert.ToString((char)obj)); };

                BaseExportersTable[typeof(DateTime)] =
                    delegate(object obj, JsonWriter writer)
                    {
                        writer.Write(Convert.ToString((DateTime)obj,
                            DatetimeFormat));
                    };

                BaseExportersTable[typeof(decimal)] =
                    delegate(object obj, JsonWriter writer) { writer.Write((decimal)obj); };

                BaseExportersTable[typeof(sbyte)] =
                    delegate(object obj, JsonWriter writer) { writer.Write(Convert.ToInt32((sbyte)obj)); };

                BaseExportersTable[typeof(short)] =
                    delegate(object obj, JsonWriter writer) { writer.Write(Convert.ToInt32((short)obj)); };

                BaseExportersTable[typeof(ushort)] =
                    delegate(object obj, JsonWriter writer) { writer.Write(Convert.ToInt32((ushort)obj)); };

                BaseExportersTable[typeof(uint)] =
                    delegate(object obj, JsonWriter writer) { writer.Write(Convert.ToUInt64((uint)obj)); };

                BaseExportersTable[typeof(ulong)] =
                    delegate(object obj, JsonWriter writer) { writer.Write((ulong)obj); };

                BaseExportersTable[typeof(DateTimeOffset)] =
                    delegate(object obj, JsonWriter writer)
                    {
                        writer.Write(((DateTimeOffset)obj).ToString("yyyy-MM-ddTHH:mm:ss.fffffffzzz", DatetimeFormat));
                    };
            }

            private static void RegisterBaseImporters()
            {
                // This method is only called from the static initializer,
                // so there is no need to explicitly lock any static members here
                ImporterFunc importer;

                importer = input => Convert.ToByte((int)input);
                RegisterImporter(BaseImportersTable, typeof(int),
                    typeof(byte), importer);

                importer = input => Convert.ToUInt64((int)input);
                RegisterImporter(BaseImportersTable, typeof(int),
                    typeof(ulong), importer);

                importer = input => Convert.ToInt64((int)input);
                RegisterImporter(BaseImportersTable, typeof(int),
                    typeof(long), importer);

                importer = input => Convert.ToSByte((int)input);
                RegisterImporter(BaseImportersTable, typeof(int),
                    typeof(sbyte), importer);

                importer = input => Convert.ToInt16((int)input);
                RegisterImporter(BaseImportersTable, typeof(int),
                    typeof(short), importer);

                importer = input => Convert.ToUInt16((int)input);
                RegisterImporter(BaseImportersTable, typeof(int),
                    typeof(ushort), importer);

                importer = input => Convert.ToUInt32((int)input);
                RegisterImporter(BaseImportersTable, typeof(int),
                    typeof(uint), importer);

                importer = input => Convert.ToSingle((int)input);
                RegisterImporter(BaseImportersTable, typeof(int),
                    typeof(float), importer);

                importer = input => Convert.ToDouble((int)input);
                RegisterImporter(BaseImportersTable, typeof(int),
                    typeof(double), importer);

                importer = input => Convert.ToDecimal((double)input);
                RegisterImporter(BaseImportersTable, typeof(double),
                    typeof(decimal), importer);

                importer = input => Convert.ToSingle((double)input);
                RegisterImporter(BaseImportersTable, typeof(double),
                    typeof(float), importer);

                importer = input => Convert.ToUInt32((long)input);
                RegisterImporter(BaseImportersTable, typeof(long),
                    typeof(uint), importer);

                importer = input => Convert.ToChar((string)input);
                RegisterImporter(BaseImportersTable, typeof(string),
                    typeof(char), importer);

                importer = input => Convert.ToDateTime((string)input, DatetimeFormat);
                RegisterImporter(BaseImportersTable, typeof(string),
                    typeof(DateTime), importer);

                importer = input => DateTimeOffset.Parse((string)input, DatetimeFormat);
                RegisterImporter(BaseImportersTable, typeof(string),
                    typeof(DateTimeOffset), importer);
            }

            private static void RegisterImporter(
                IDictionary<Type, IDictionary<Type, ImporterFunc>> table,
                Type jsonType, Type valueType, ImporterFunc importer)
            {
                if (!table.ContainsKey(jsonType))
                    table.Add(jsonType, new Dictionary<Type, ImporterFunc>());

                table[jsonType][valueType] = importer;
            }

            private static void WriteValue(object? obj, JsonWriter writer,
                bool writerIsPrivate, int depth)
            {
                if (depth > MaxNestingDepth)
                    throw new JsonException(
                        "Max allowed object depth reached while " + $"trying to export from type {obj?.GetType()}");

                if (obj == null)
                {
                    writer.Write(null);
                    return;
                }

                if (obj is IJsonWrapper wrapper)
                {
                    if (writerIsPrivate)
                        writer.TextWriter.Write(wrapper.ToJson());
                    else
                        wrapper.ToJson(writer);

                    return;
                }

                if (obj is string s)
                {
                    writer.Write(s);
                    return;
                }

                if (obj is double d)
                {
                    writer.Write(d);
                    return;
                }

                if (obj is decimal dec)
                {
                    writer.Write(dec);
                    return;
                }

                if (obj is float f)
                {
                    writer.Write(f);
                    return;
                }

                if (obj is int i)
                {
                    writer.Write(i);
                    return;
                }

                if (obj is bool b)
                {
                    writer.Write(b);
                    return;
                }

                if (obj is long l)
                {
                    writer.Write(l);
                    return;
                }

                if (obj is Array array)
                {
                    writer.WriteArrayStart();

                    foreach (var elem in array)
                        WriteValue(elem, writer, writerIsPrivate, depth + 1);

                    writer.WriteArrayEnd();

                    return;
                }

                if (obj is IList list)
                {
                    writer.WriteArrayStart();
                    foreach (var elem in list)
                        WriteValue(elem, writer, writerIsPrivate, depth + 1);
                    writer.WriteArrayEnd();

                    return;
                }

                if (obj is IDictionary dictionary)
                {
                    writer.WriteObjectStart();
                    foreach (DictionaryEntry entry in dictionary)
                    {
                        if (entry.Key == null)
                            throw new JsonException("Dictionary keys must not be null");

                        var propertyName = entry.Key as string
                                           ?? Convert.ToString(entry.Key, CultureInfo.InvariantCulture) ??
                                           throw new JsonException(
                                               "Dictionary keys must be convertible to non-null strings");
                        writer.WritePropertyName(propertyName);
                        WriteValue(entry.Value, writer, writerIsPrivate,
                            depth + 1);
                    }

                    writer.WriteObjectEnd();

                    return;
                }

                var objType = obj.GetType();

                // See if there's a custom exporter for the object
                lock (CustomExportersTableLock)
                {
                    if (CustomExportersTable.TryGetValue(objType, out var customExporter))
                    {
                        customExporter(obj, writer);

                        return;
                    }
                }

                // If not, maybe there's a base exporter
                if (BaseExportersTable.TryGetValue(objType, out var baseExporter))
                {
                    baseExporter(obj, writer);

                    return;
                }

                // Last option, let's see if it's an enum
                if (obj is Enum)
                {
                    var eType = Enum.GetUnderlyingType(objType);

                    if (eType == typeof(long))
                        writer.Write((long)obj);
                    else if (eType == typeof(uint))
                        writer.Write((uint)obj);
                    else if (eType == typeof(ulong))
                        writer.Write((ulong)obj);
                    else if (eType == typeof(ushort))
                        writer.Write((ushort)obj);
                    else if (eType == typeof(short))
                        writer.Write((short)obj);
                    else if (eType == typeof(byte))
                        writer.Write((byte)obj);
                    else if (eType == typeof(sbyte))
                        writer.Write((sbyte)obj);
                    else
                        writer.Write((int)obj);

                    return;
                }

                // Okay, so it looks like the input should be exported as an
                // object
                AddTypeProperties(objType);
                var props = TypeProperties[objType];

                writer.WriteObjectStart();
                foreach (var pData in props)
                {
                    if (pData.IsField)
                    {
                        writer.WritePropertyName(pData.Info.Name);
                        WriteValue(((FieldInfo)pData.Info).GetValue(obj),
                            writer, writerIsPrivate, depth + 1);
                    }
                    else
                    {
                        var pInfo = (PropertyInfo)pData.Info;

                        if (pInfo.CanRead)
                        {
                            writer.WritePropertyName(pData.Info.Name);
                            WriteValue(pInfo.GetValue(obj, null),
                                writer, writerIsPrivate, depth + 1);
                        }
                    }
                }

                writer.WriteObjectEnd();
            }

            #endregion


            /// <summary>
            /// Serializes a CLR value to JSON text using LitJson's default mapping rules.
            /// </summary>
            /// <param name="obj">
            /// Source value. <see langword="null"/> is serialized as JSON <c>null</c>.
            /// Arrays, lists, dictionaries, wrappers, primitives, enums, and reflected objects are supported.
            /// </param>
            /// <returns>A compact JSON string.</returns>
            public static string ToJson(object? obj)
            {
                lock (StaticWriterLock)
                {
                    StaticWriter.Reset();
                    var oldValidate = StaticWriter.Validate;
                    StaticWriter.Validate = false;
                    try
                    {
                        WriteValue(obj, StaticWriter, true, 0);
                        return StaticWriter.ToString();
                    }
                    finally
                    {
                        StaticWriter.Validate = oldValidate;
                    }
                }
            }

            /// <summary>
            /// Serializes a CLR value into an existing <see cref="JsonWriter"/>.
            /// </summary>
            /// <param name="obj">Source value to serialize.</param>
            /// <param name="writer">Destination writer that receives the JSON output.</param>
            public static void ToJson(object? obj, JsonWriter writer)
            {
                WriteValue(obj, writer, false, 0);
            }

            /// <summary>
            /// Reads a JSON value from a <see cref="JsonReader"/> into a <see cref="JsonData"/> tree.
            /// </summary>
            /// <param name="reader">Reader positioned before the next JSON value.</param>
            /// <returns>
            /// A populated <see cref="JsonData"/> tree, or <see langword="null"/> when the JSON token
            /// itself is <c>null</c>.
            /// </returns>
            public static JsonData? ToObject(JsonReader reader)
            {
                return (JsonData?)ToWrapper(
                    delegate { return new JsonData(); }, reader);
            }

            /// <summary>
            /// Reads JSON text from a <see cref="TextReader"/> into a <see cref="JsonData"/> tree.
            /// </summary>
            /// <param name="reader">Source text reader containing JSON.</param>
            /// <returns>A <see cref="JsonData"/> tree or <see langword="null"/> for JSON <c>null</c>.</returns>
            public static JsonData? ToObject(TextReader reader)
            {
                var jsonReader = new JsonReader(reader);

                return (JsonData?)ToWrapper(
                    delegate { return new JsonData(); }, jsonReader);
            }

            /// <summary>
            /// Parses JSON text into a <see cref="JsonData"/> tree.
            /// </summary>
            /// <param name="json">JSON text to parse.</param>
            /// <returns>A <see cref="JsonData"/> tree or <see langword="null"/> for JSON <c>null</c>.</returns>
            public static JsonData? ToObject(string json)
            {
                return (JsonData?)ToWrapper(
                    delegate { return new JsonData(); }, json);
            }

            /// <summary>
            /// Deserializes the next JSON value from a <see cref="JsonReader"/> into the specified CLR type.
            /// </summary>
            /// <typeparam name="T">Requested destination CLR type.</typeparam>
            /// <param name="reader">Reader positioned before the next JSON value.</param>
            /// <returns>
            /// The deserialized value, or <see langword="null"/> / <see langword="default"/> when the
            /// JSON token is <c>null</c> and the destination type allows it.
            /// </returns>
            /// <remarks>
            /// This API applies LitJson's importer table, enum conversion rules, implicit operators,
            /// and reflection-based object mapping.
            /// </remarks>
            public static T? ToObject<T>(JsonReader reader)
            {
                var value = ReadValue(typeof(T), reader);
                return value == null ? default : (T)value;
            }

            /// <summary>
            /// Deserializes JSON text from a <see cref="TextReader"/> into the specified CLR type.
            /// </summary>
            /// <typeparam name="T">Requested destination CLR type.</typeparam>
            /// <param name="reader">Reader that provides JSON text.</param>
            /// <returns>The deserialized value.</returns>
            public static T? ToObject<T>(TextReader reader)
            {
                var jsonReader = new JsonReader(reader);

                var value = ReadValue(typeof(T), jsonReader);
                return value == null ? default : (T)value;
            }

            /// <summary>
            /// Deserializes JSON text into the specified CLR type.
            /// </summary>
            /// <typeparam name="T">Requested destination CLR type.</typeparam>
            /// <param name="json">JSON text to deserialize.</param>
            /// <returns>The deserialized value.</returns>
            public static T? ToObject<T>(string json)
            {
                var reader = new JsonReader(json);

                var value = ReadValue(typeof(T), reader);
                return value == null ? default : (T)value;
            }

            /// <summary>
            /// Deserializes JSON text into a runtime-specified CLR type.
            /// </summary>
            /// <param name="json">JSON text to deserialize.</param>
            /// <param name="convertType">Destination CLR type known only at runtime.</param>
            /// <returns>The deserialized value, or <see langword="null"/> for JSON <c>null</c>.</returns>
            public static object? ToObject(string json, Type convertType)
            {
                var reader = new JsonReader(json);

                return ReadValue(convertType, reader);
            }

            /// <summary>
            /// Deserializes the next JSON value from a reader into a caller-provided wrapper implementation.
            /// </summary>
            /// <param name="factory">
            /// Factory used to create wrapper instances for each non-null JSON node encountered.
            /// </param>
            /// <param name="reader">Reader positioned before the next JSON value.</param>
            /// <returns>A wrapper graph, or <see langword="null"/> for JSON <c>null</c>.</returns>
            public static IJsonWrapper? ToWrapper(WrapperFactory factory,
                JsonReader reader)
            {
                return ReadValue(factory, reader);
            }

            /// <summary>
            /// Deserializes JSON text into a caller-provided wrapper implementation.
            /// </summary>
            /// <param name="factory">Factory used to create wrapper instances.</param>
            /// <param name="json">JSON text to parse.</param>
            /// <returns>A wrapper graph, or <see langword="null"/> for JSON <c>null</c>.</returns>
            public static IJsonWrapper? ToWrapper(WrapperFactory factory,
                string json)
            {
                var reader = new JsonReader(json);

                return ReadValue(factory, reader);
            }

            /// <summary>
            /// Registers a custom exporter for a CLR type.
            /// </summary>
            /// <typeparam name="T">CLR type handled by the exporter.</typeparam>
            /// <param name="exporter">
            /// Callback that writes a value of type <typeparamref name="T"/> to a <see cref="JsonWriter"/>.
            /// </param>
            /// <remarks>
            /// Custom exporters override the built-in exporter lookup for the exact registered type.
            /// </remarks>
            public static void RegisterExporter<T>(ExporterFunc<T> exporter)
            {
                lock (CustomExportersTableLock)
                {
                    CustomExportersTable[typeof(T)] = delegate(object obj, JsonWriter writer)
                    {
                        exporter((T)obj, writer);
                    };
                }
            }

            /// <summary>
            /// Registers a custom importer that converts one parsed JSON CLR representation into another CLR type.
            /// </summary>
            /// <typeparam name="TJson">
            /// The intermediate CLR type produced by the parser, such as <see cref="int"/>,
            /// <see cref="long"/>, <see cref="double"/>, <see cref="bool"/>, or <see cref="string"/>.
            /// </typeparam>
            /// <typeparam name="TValue">Destination CLR type.</typeparam>
            /// <param name="importer">Conversion callback invoked during deserialization.</param>
            public static void RegisterImporter<TJson, TValue>(
                ImporterFunc<TJson, TValue> importer)
            {
                lock (CustomImportersTableLock)
                {
                    RegisterImporter(CustomImportersTable, typeof(TJson),
                        typeof(TValue), input => importer((TJson)input));
                }
            }

            /// <summary>
            /// Removes all custom exporters previously registered through <see cref="RegisterExporter{T}"/>.
            /// </summary>
            public static void UnregisterExporters()
            {
                lock (CustomExportersTableLock)
                {
                    CustomExportersTable.Clear();
                }
            }

            /// <summary>
            /// Removes all custom importers previously registered through <see cref="RegisterImporter{TJson, TValue}"/>.
            /// </summary>
            public static void UnregisterImporters()
            {
                lock (CustomImportersTableLock)
                {
                    CustomImportersTable.Clear();
                }
            }
        }

        #endregion

        #region JsonMockWrapper.g.cs

        public class JsonMockWrapper : IJsonWrapper
        {
            public bool IsArray => false;

            public bool IsBoolean => false;

            public bool IsDouble => false;

            public bool IsInt => false;

            public bool IsLong => false;

            public bool IsObject => false;

            public bool IsString => false;

            public bool GetBoolean()
            {
                return false;
            }

            public double GetDouble()
            {
                return 0.0;
            }

            public int GetInt()
            {
                return 0;
            }

            public JsonType GetJsonType()
            {
                return JsonType.None;
            }

            public long GetLong()
            {
                return 0L;
            }

            public string GetString()
            {
                return "";
            }

            public void SetBoolean(bool val)
            {
            }

            public void SetDouble(double val)
            {
            }

            public void SetInt(int val)
            {
            }

            public void SetJsonType(JsonType type)
            {
            }

            public void SetLong(long val)
            {
            }

            public void SetString(string val)
            {
            }

            public string ToJson()
            {
                return "";
            }

            public void ToJson(JsonWriter writer)
            {
            }


            bool IList.IsFixedSize => true;

            bool IList.IsReadOnly => true;

            object? IList.this[int index]
            {
                get => null;
                set { }
            }

            int IList.Add(object? value)
            {
                return 0;
            }

            void IList.Clear()
            {
            }

            bool IList.Contains(object? value)
            {
                return false;
            }

            int IList.IndexOf(object? value)
            {
                return -1;
            }

            void IList.Insert(int i, object? v)
            {
            }

            void IList.Remove(object? value)
            {
            }

            void IList.RemoveAt(int index)
            {
            }


            int ICollection.Count => 0;

            bool ICollection.IsSynchronized => false;

            object ICollection.SyncRoot => this;

            void ICollection.CopyTo(Array array, int index)
            {
            }


            IEnumerator IEnumerable.GetEnumerator()
            {
                return Array.Empty<object>().GetEnumerator();
            }


            bool IDictionary.IsFixedSize => true;

            bool IDictionary.IsReadOnly => true;

            ICollection IDictionary.Keys => Array.Empty<object>();

            ICollection IDictionary.Values => Array.Empty<object>();

            object? IDictionary.this[object key]
            {
                get => null;
                set { }
            }

            void IDictionary.Add(object key, object? value)
            {
            }

            void IDictionary.Clear()
            {
            }

            bool IDictionary.Contains(object key)
            {
                return false;
            }

            void IDictionary.Remove(object key)
            {
            }

            IDictionaryEnumerator IDictionary.GetEnumerator()
            {
                return new OrderedDictionaryEnumerator(
                    ((IEnumerable<KeyValuePair<string, JsonData?>>)Array.Empty<KeyValuePair<string, JsonData?>>())
                    .GetEnumerator());
            }


            object? IOrderedDictionary.this[int idx]
            {
                get => null;
                set { }
            }

            IDictionaryEnumerator IOrderedDictionary.GetEnumerator()
            {
                return ((IDictionary)this).GetEnumerator();
            }

            void IOrderedDictionary.Insert(int i, object k, object? v)
            {
            }

            void IOrderedDictionary.RemoveAt(int i)
            {
            }
        }

        #endregion

        #region JsonReader.g.cs

        /// <summary>
        /// Token kinds emitted by <see cref="JsonReader"/> while walking JSON text.
        /// </summary>
        /// <remarks>
        /// Tokens represent the streaming parser view of the input, not the wrapper model used by
        /// <see cref="JsonData"/>.
        /// </remarks>
        public enum JsonToken
        {
            None,

            ObjectStart,
            PropertyName,
            ObjectEnd,

            ArrayStart,
            ArrayEnd,

            Int,
            Long,
            Double,

            String,

            Boolean,
            Null
        }


        /// <summary>
        /// Forward-only streaming JSON reader.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Each successful call to <see cref="Read"/> advances the parser to the next token and updates
        /// <see cref="Token"/> plus <see cref="Value"/>.
        /// </para>
        /// <para>
        /// Primitive token payloads are exposed through <see cref="Value"/> using CLR values such as
        /// <see cref="string"/>, <see cref="int"/>, <see cref="long"/>, <see cref="double"/>, and
        /// <see cref="bool"/>.
        /// </para>
        /// </remarks>
        public class JsonReader
        {
            #region Fields

            private static readonly IDictionary<int, IDictionary<int, int[]>> ParseTable;

            private readonly Stack<int> _automatonStack;
            private int _currentInput;
            private int _currentSymbol;
            private bool _endOfJson;
            private bool _endOfInput;
            private readonly Lexer _lexer;
            private bool _parserInString;
            private bool _parserReturn;
            private bool _readStarted;
            private TextReader? _reader;
            private bool _readerIsOwned;
            private bool _skipNonMembers;
            private object? _tokenValue;
            private JsonToken _token;

            #endregion


            #region Public Properties

            public bool AllowComments
            {
                get => _lexer.AllowComments;
                set => _lexer.AllowComments = value;
            }

            public bool AllowSingleQuotedStrings
            {
                get => _lexer.AllowSingleQuotedStrings;
                set => _lexer.AllowSingleQuotedStrings = value;
            }

            public bool SkipNonMembers
            {
                get => _skipNonMembers;
                set => _skipNonMembers = value;
            }

            public bool EndOfInput => _endOfInput;

            public bool EndOfJson => _endOfJson;

            public JsonToken Token => _token;

            public object? Value => _tokenValue;

            #endregion


            #region Constructors

            static JsonReader()
            {
                ParseTable = PopulateParseTable();
            }

            /// <summary>
            /// Creates a reader over an in-memory JSON string.
            /// </summary>
            /// <param name="jsonText">JSON text to parse.</param>
            public JsonReader(string jsonText) :
                this(new StringReader(jsonText), true)
            {
            }

            /// <summary>
            /// Creates a reader over an existing <see cref="TextReader"/>.
            /// </summary>
            /// <param name="reader">Source text reader.</param>
            public JsonReader(TextReader reader) :
                this(reader, false)
            {
            }

            private JsonReader(TextReader reader, bool owned)
            {
                if (reader == null)
                    throw new ArgumentNullException("reader");

                _parserInString = false;
                _parserReturn = false;

                _readStarted = false;
                _automatonStack = new Stack<int>();
                _automatonStack.Push((int)ParserToken.End);
                _automatonStack.Push((int)ParserToken.Text);

                _lexer = new Lexer(reader);

                _endOfInput = false;
                _endOfJson = false;

                _skipNonMembers = true;

                _reader = reader;
                _readerIsOwned = owned;
            }

            #endregion


            #region Static Methods

            private static IDictionary<int, IDictionary<int, int[]>> PopulateParseTable()
            {
                // See section A.2. of the manual for details
                IDictionary<int, IDictionary<int, int[]>> parseTable = new Dictionary<int, IDictionary<int, int[]>>();

                TableAddRow(parseTable, ParserToken.Array);
                TableAddCol(parseTable, ParserToken.Array, '[',
                    '[',
                    (int)ParserToken.ArrayPrime);

                TableAddRow(parseTable, ParserToken.ArrayPrime);
                TableAddCol(parseTable, ParserToken.ArrayPrime, '"',
                    (int)ParserToken.Value,
                    (int)ParserToken.ValueRest,
                    ']');
                TableAddCol(parseTable, ParserToken.ArrayPrime, '[',
                    (int)ParserToken.Value,
                    (int)ParserToken.ValueRest,
                    ']');
                TableAddCol(parseTable, ParserToken.ArrayPrime, ']',
                    ']');
                TableAddCol(parseTable, ParserToken.ArrayPrime, '{',
                    (int)ParserToken.Value,
                    (int)ParserToken.ValueRest,
                    ']');
                TableAddCol(parseTable, ParserToken.ArrayPrime, (int)ParserToken.Number,
                    (int)ParserToken.Value,
                    (int)ParserToken.ValueRest,
                    ']');
                TableAddCol(parseTable, ParserToken.ArrayPrime, (int)ParserToken.True,
                    (int)ParserToken.Value,
                    (int)ParserToken.ValueRest,
                    ']');
                TableAddCol(parseTable, ParserToken.ArrayPrime, (int)ParserToken.False,
                    (int)ParserToken.Value,
                    (int)ParserToken.ValueRest,
                    ']');
                TableAddCol(parseTable, ParserToken.ArrayPrime, (int)ParserToken.Null,
                    (int)ParserToken.Value,
                    (int)ParserToken.ValueRest,
                    ']');

                TableAddRow(parseTable, ParserToken.Object);
                TableAddCol(parseTable, ParserToken.Object, '{',
                    '{',
                    (int)ParserToken.ObjectPrime);

                TableAddRow(parseTable, ParserToken.ObjectPrime);
                TableAddCol(parseTable, ParserToken.ObjectPrime, '"',
                    (int)ParserToken.Pair,
                    (int)ParserToken.PairRest,
                    '}');
                TableAddCol(parseTable, ParserToken.ObjectPrime, '}',
                    '}');

                TableAddRow(parseTable, ParserToken.Pair);
                TableAddCol(parseTable, ParserToken.Pair, '"',
                    (int)ParserToken.String,
                    ':',
                    (int)ParserToken.Value);

                TableAddRow(parseTable, ParserToken.PairRest);
                TableAddCol(parseTable, ParserToken.PairRest, ',',
                    ',',
                    (int)ParserToken.Pair,
                    (int)ParserToken.PairRest);
                TableAddCol(parseTable, ParserToken.PairRest, '}',
                    (int)ParserToken.Epsilon);

                TableAddRow(parseTable, ParserToken.String);
                TableAddCol(parseTable, ParserToken.String, '"',
                    '"',
                    (int)ParserToken.CharSeq,
                    '"');

                TableAddRow(parseTable, ParserToken.Text);
                TableAddCol(parseTable, ParserToken.Text, '[',
                    (int)ParserToken.Array);
                TableAddCol(parseTable, ParserToken.Text, '{',
                    (int)ParserToken.Object);

                TableAddRow(parseTable, ParserToken.Value);
                TableAddCol(parseTable, ParserToken.Value, '"',
                    (int)ParserToken.String);
                TableAddCol(parseTable, ParserToken.Value, '[',
                    (int)ParserToken.Array);
                TableAddCol(parseTable, ParserToken.Value, '{',
                    (int)ParserToken.Object);
                TableAddCol(parseTable, ParserToken.Value, (int)ParserToken.Number,
                    (int)ParserToken.Number);
                TableAddCol(parseTable, ParserToken.Value, (int)ParserToken.True,
                    (int)ParserToken.True);
                TableAddCol(parseTable, ParserToken.Value, (int)ParserToken.False,
                    (int)ParserToken.False);
                TableAddCol(parseTable, ParserToken.Value, (int)ParserToken.Null,
                    (int)ParserToken.Null);

                TableAddRow(parseTable, ParserToken.ValueRest);
                TableAddCol(parseTable, ParserToken.ValueRest, ',',
                    ',',
                    (int)ParserToken.Value,
                    (int)ParserToken.ValueRest);
                TableAddCol(parseTable, ParserToken.ValueRest, ']',
                    (int)ParserToken.Epsilon);

                return parseTable;
            }

            private static void TableAddCol(IDictionary<int, IDictionary<int, int[]>> parseTable, ParserToken row,
                int col,
                params int[] symbols)
            {
                parseTable[(int)row].Add(col, symbols);
            }

            private static void TableAddRow(IDictionary<int, IDictionary<int, int[]>> parseTable, ParserToken rule)
            {
                parseTable.Add((int)rule, new Dictionary<int, int[]>());
            }

            #endregion


            #region Private Methods

            private void ProcessNumber(string number)
            {
                if (number.IndexOf('.') != -1 ||
                    number.IndexOf('e') != -1 ||
                    number.IndexOf('E') != -1)
                {
                    if (double.TryParse(number, NumberStyles.Any, CultureInfo.InvariantCulture, out var nDouble))
                    {
                        _token = JsonToken.Double;
                        _tokenValue = nDouble;

                        return;
                    }
                }

                if (int.TryParse(number, NumberStyles.Integer, CultureInfo.InvariantCulture, out var nINT32))
                {
                    _token = JsonToken.Int;
                    _tokenValue = nINT32;

                    return;
                }

                if (long.TryParse(number, NumberStyles.Integer, CultureInfo.InvariantCulture, out var nINT64))
                {
                    _token = JsonToken.Long;
                    _tokenValue = nINT64;

                    return;
                }

                if (ulong.TryParse(number, NumberStyles.Integer, CultureInfo.InvariantCulture, out var nUint64))
                {
                    _token = JsonToken.Long;
                    _tokenValue = nUint64;

                    return;
                }

                // Shouldn't happen, but just in case, return something
                _token = JsonToken.Int;
                _tokenValue = 0;
            }

            private void ProcessSymbol()
            {
                if (_currentSymbol == '[')
                {
                    _token = JsonToken.ArrayStart;
                    _parserReturn = true;
                }
                else if (_currentSymbol == ']')
                {
                    _token = JsonToken.ArrayEnd;
                    _parserReturn = true;
                }
                else if (_currentSymbol == '{')
                {
                    _token = JsonToken.ObjectStart;
                    _parserReturn = true;
                }
                else if (_currentSymbol == '}')
                {
                    _token = JsonToken.ObjectEnd;
                    _parserReturn = true;
                }
                else if (_currentSymbol == '"')
                {
                    if (_parserInString)
                    {
                        _parserInString = false;

                        _parserReturn = true;
                    }
                    else
                    {
                        if (_token == JsonToken.None)
                            _token = JsonToken.String;

                        _parserInString = true;
                    }
                }
                else if (_currentSymbol == (int)ParserToken.CharSeq)
                {
                    _tokenValue = _lexer.StringValue;
                }
                else if (_currentSymbol == (int)ParserToken.False)
                {
                    _token = JsonToken.Boolean;
                    _tokenValue = false;
                    _parserReturn = true;
                }
                else if (_currentSymbol == (int)ParserToken.Null)
                {
                    _token = JsonToken.Null;
                    _parserReturn = true;
                }
                else if (_currentSymbol == (int)ParserToken.Number)
                {
                    ProcessNumber(_lexer.StringValue);

                    _parserReturn = true;
                }
                else if (_currentSymbol == (int)ParserToken.Pair)
                {
                    _token = JsonToken.PropertyName;
                }
                else if (_currentSymbol == (int)ParserToken.True)
                {
                    _token = JsonToken.Boolean;
                    _tokenValue = true;
                    _parserReturn = true;
                }
            }

            private bool ReadToken()
            {
                if (_endOfInput)
                    return false;

                _lexer.NextToken();

                if (_lexer.EndOfInput)
                {
                    Close();

                    return false;
                }

                _currentInput = _lexer.Token;

                return true;
            }

            #endregion


            /// <summary>
            /// Marks the reader as finished and releases the underlying text source when owned.
            /// </summary>
            /// <remarks>
            /// Readers created from a JSON string own their internal <see cref="StringReader"/> and will
            /// close it here. Readers created from an external <see cref="TextReader"/> leave ownership
            /// with the caller.
            /// </remarks>
            public void Close()
            {
                if (_endOfInput)
                    return;

                _endOfInput = true;
                _endOfJson = true;

                if (_readerIsOwned)
                {
                    using (_reader)
                    {
                    }
                }

                _reader = null;
            }

            /// <summary>
            /// Advances to the next token in the JSON stream.
            /// </summary>
            /// <returns>
            /// <see langword="true"/> if a token was produced; otherwise <see langword="false"/> when
            /// the input is exhausted.
            /// </returns>
            /// <remarks>
            /// After a successful call, inspect <see cref="Token"/> and, for primitive tokens,
            /// <see cref="Value"/>. Structural tokens such as array/object start and end update
            /// <see cref="Token"/> but do not carry a payload.
            /// </remarks>
            public bool Read()
            {
                if (_endOfInput)
                    return false;

                if (_endOfJson)
                {
                    _endOfJson = false;
                    _automatonStack.Clear();
                    _automatonStack.Push((int)ParserToken.End);
                    _automatonStack.Push((int)ParserToken.Text);
                }

                _parserInString = false;
                _parserReturn = false;

                _token = JsonToken.None;
                _tokenValue = null;

                if (!_readStarted)
                {
                    _readStarted = true;

                    if (!ReadToken())
                        return false;
                }


                int[] entrySymbols;

                while (true)
                {
                    if (_parserReturn)
                    {
                        if (_automatonStack.Peek() == (int)ParserToken.End)
                            _endOfJson = true;

                        return true;
                    }

                    _currentSymbol = _automatonStack.Pop();

                    ProcessSymbol();

                    if (_currentSymbol == _currentInput)
                    {
                        if (!ReadToken())
                        {
                            if (_automatonStack.Peek() != (int)ParserToken.End)
                                throw new JsonException(
                                    "Input doesn't evaluate to proper JSON text");

                            if (_parserReturn)
                                return true;

                            return false;
                        }

                        continue;
                    }

                    try
                    {
                        entrySymbols =
                            ParseTable[_currentSymbol][_currentInput];
                    }
                    catch (KeyNotFoundException e)
                    {
                        throw new JsonException((ParserToken)_currentInput, e);
                    }

                    if (entrySymbols[0] == (int)ParserToken.Epsilon)
                        continue;

                    for (var i = entrySymbols.Length - 1; i >= 0; i--)
                        _automatonStack.Push(entrySymbols[i]);
                }
            }
        }

        #endregion

        #region JsonWriter.g.cs

        internal enum Condition
        {
            InArray,
            InObject,
            NotAProperty,
            Property,
            Value
        }

        internal class WriterContext
        {
            public int Count;
            public bool InArray;
            public bool InObject;
            public bool ExpectingValue;
            public int Padding;
        }

        /// <summary>
        /// Streaming JSON writer that incrementally emits valid JSON text.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The writer tracks object/array nesting and, when <see cref="Validate"/> is enabled, enforces
        /// correct JSON write order such as property-name followed by value.
        /// </para>
        /// <para>
        /// The various <c>Write</c> overloads emit primitive values, while
        /// <see cref="WriteArrayStart"/> / <see cref="WriteArrayEnd"/> and
        /// <see cref="WriteObjectStart"/> / <see cref="WriteObjectEnd"/> control structured output.
        /// </para>
        /// </remarks>
        public class JsonWriter
        {
            #region Fields

            private static readonly NumberFormatInfo NumberFormat;

            private WriterContext _context = null!;
            private Stack<WriterContext> _ctxStack = null!;
            private bool _hasReachedEnd;
            private char[] _hexSeq = null!;
            private int _indentation;
            private int _indentValue;
            private StringBuilder? _instStringBuilder;
            private bool _prettyPrint;
            private bool _validate;
            private bool _lowerCaseProperties;
            private TextWriter _writer;

            #endregion


            #region Properties

            public int IndentValue
            {
                get => _indentValue;
                set
                {
                    _indentation = (_indentation / _indentValue) * value;
                    _indentValue = value;
                }
            }

            public bool PrettyPrint
            {
                get => _prettyPrint;
                set => _prettyPrint = value;
            }

            public TextWriter TextWriter => _writer;

            public bool Validate
            {
                get => _validate;
                set => _validate = value;
            }

            public bool LowerCaseProperties
            {
                get => _lowerCaseProperties;
                set => _lowerCaseProperties = value;
            }

            #endregion


            #region Constructors

            static JsonWriter()
            {
                NumberFormat = NumberFormatInfo.InvariantInfo;
            }

            /// <summary>
            /// Creates a writer backed by an internal <see cref="StringBuilder"/>.
            /// </summary>
            /// <remarks>
            /// Use <see cref="ToString"/> to retrieve the accumulated JSON text.
            /// </remarks>
            public JsonWriter()
            {
                _instStringBuilder = new StringBuilder();
                _writer = new StringWriter(_instStringBuilder);

                Init();
            }

            /// <summary>
            /// Creates a writer that appends JSON text into the provided <see cref="StringBuilder"/>.
            /// </summary>
            /// <param name="sb">Destination string builder.</param>
            public JsonWriter(StringBuilder sb) :
                this(new StringWriter(sb))
            {
            }

            /// <summary>
            /// Creates a writer that emits JSON into an existing <see cref="TextWriter"/>.
            /// </summary>
            /// <param name="writer">Destination text writer.</param>
            public JsonWriter(TextWriter writer)
            {
                _writer = writer ?? throw new ArgumentNullException("writer");

                Init();
            }

            #endregion


            #region Private Methods

            private void DoValidation(Condition cond)
            {
                if (!_context.ExpectingValue)
                    _context.Count++;

                if (!_validate)
                    return;

                if (_hasReachedEnd)
                    throw new JsonException(
                        "A complete JSON symbol has already been written");

                switch (cond)
                {
                    case Condition.InArray:
                        if (!_context.InArray)
                            throw new JsonException(
                                "Can't close an array here");
                        break;

                    case Condition.InObject:
                        if (!_context.InObject || _context.ExpectingValue)
                            throw new JsonException(
                                "Can't close an object here");
                        break;

                    case Condition.NotAProperty:
                        if (_context.InObject && !_context.ExpectingValue)
                            throw new JsonException(
                                "Expected a property");
                        break;

                    case Condition.Property:
                        if (!_context.InObject || _context.ExpectingValue)
                            throw new JsonException(
                                "Can't add a property here");
                        break;

                    case Condition.Value:
                        if (!_context.InArray &&
                            (!_context.InObject || !_context.ExpectingValue))
                            throw new JsonException(
                                "Can't add a value here");

                        break;
                }
            }

            private void Init()
            {
                _hasReachedEnd = false;
                _hexSeq = new char[4];
                _indentation = 0;
                _indentValue = 4;
                _prettyPrint = false;
                _validate = true;
                _lowerCaseProperties = false;

                _ctxStack = new Stack<WriterContext>();
                _context = new WriterContext();
                _ctxStack.Push(_context);
            }

            private static void IntToHex(int n, char[] hex)
            {
                int num;

                for (var i = 0; i < 4; i++)
                {
                    num = n % 16;

                    if (num < 10)
                        hex[3 - i] = (char)('0' + num);
                    else
                        hex[3 - i] = (char)('A' + (num - 10));

                    n >>= 4;
                }
            }

            private void Indent()
            {
                if (_prettyPrint)
                    _indentation += _indentValue;
            }


            private void Put(string str)
            {
                if (_prettyPrint && !_context.ExpectingValue)
                    for (var i = 0; i < _indentation; i++)
                        _writer.Write(' ');

                _writer.Write(str);
            }

            private void PutNewline()
            {
                PutNewline(true);
            }

            private void PutNewline(bool addComma)
            {
                if (addComma && !_context.ExpectingValue &&
                    _context.Count > 1)
                    _writer.Write(',');

                if (_prettyPrint && !_context.ExpectingValue)
                    _writer.Write(Environment.NewLine);
            }

            private void PutString(string str)
            {
                Put(string.Empty);

                _writer.Write('"');

                var n = str.Length;
                for (var i = 0; i < n; i++)
                {
                    switch (str[i])
                    {
                        case '\n':
                            _writer.Write("\\n");
                            continue;

                        case '\r':
                            _writer.Write("\\r");
                            continue;

                        case '\t':
                            _writer.Write("\\t");
                            continue;

                        case '"':
                        case '\\':
                            _writer.Write('\\');
                            _writer.Write(str[i]);
                            continue;

                        case '\f':
                            _writer.Write("\\f");
                            continue;

                        case '\b':
                            _writer.Write("\\b");
                            continue;
                    }

                    if (str[i] >= 32 && str[i] <= 126)
                    {
                        _writer.Write(str[i]);
                        continue;
                    }

                    // Default, turn into a \uXXXX sequence
                    IntToHex(str[i], _hexSeq);
                    _writer.Write("\\u");
                    _writer.Write(_hexSeq);
                }

                _writer.Write('"');
            }

            private void Unindent()
            {
                if (_prettyPrint)
                    _indentation -= _indentValue;
            }

            #endregion


            public override string ToString()
            {
                if (_instStringBuilder == null)
                    return string.Empty;

                return _instStringBuilder.ToString();
            }

            /// <summary>
            /// Clears the current output state so the writer can be reused for a new JSON value.
            /// </summary>
            /// <remarks>
            /// Structural nesting state is reset together with any buffered output held by an internal
            /// string builder.
            /// </remarks>
            public void Reset()
            {
                _hasReachedEnd = false;

                _ctxStack.Clear();
                _context = new WriterContext();
                _ctxStack.Push(_context);

                if (_instStringBuilder != null)
                    _instStringBuilder.Remove(0, _instStringBuilder.Length);
            }

            public void Write(bool boolean)
            {
                DoValidation(Condition.Value);
                PutNewline();

                Put(boolean ? "true" : "false");

                _context.ExpectingValue = false;
            }

            public void Write(decimal number)
            {
                DoValidation(Condition.Value);
                PutNewline();

                Put(Convert.ToString(number, NumberFormat));

                _context.ExpectingValue = false;
            }

            public void Write(double number)
            {
                DoValidation(Condition.Value);
                PutNewline();

                var str = Convert.ToString(number, NumberFormat);
                Put(str);

                if (str.IndexOf('.') == -1 &&
                    str.IndexOf('E') == -1)
                    _writer.Write(".0");

                _context.ExpectingValue = false;
            }

            public void Write(float number)
            {
                DoValidation(Condition.Value);
                PutNewline();

                var str = Convert.ToString(number, NumberFormat);
                Put(str);

                _context.ExpectingValue = false;
            }

            public void Write(int number)
            {
                DoValidation(Condition.Value);
                PutNewline();

                Put(Convert.ToString(number, NumberFormat));

                _context.ExpectingValue = false;
            }

            public void Write(long number)
            {
                DoValidation(Condition.Value);
                PutNewline();

                Put(Convert.ToString(number, NumberFormat));

                _context.ExpectingValue = false;
            }

            /// <summary>
            /// Writes a JSON string value, or JSON <c>null</c> when <paramref name="str"/> is null.
            /// </summary>
            /// <param name="str">String value to write.</param>
            public void Write(string? str)
            {
                DoValidation(Condition.Value);
                PutNewline();

                if (str == null)
                    Put("null");
                else
                    PutString(str);

                _context.ExpectingValue = false;
            }

            public void Write(ulong number)
            {
                DoValidation(Condition.Value);
                PutNewline();

                Put(Convert.ToString(number, NumberFormat));

                _context.ExpectingValue = false;
            }

            /// <summary>
            /// Ends the current JSON array.
            /// </summary>
            public void WriteArrayEnd()
            {
                DoValidation(Condition.InArray);
                PutNewline(false);

                _ctxStack.Pop();
                if (_ctxStack.Count == 1)
                    _hasReachedEnd = true;
                else
                {
                    _context = _ctxStack.Peek();
                    _context.ExpectingValue = false;
                }

                Unindent();
                Put("]");
            }

            /// <summary>
            /// Starts a new JSON array.
            /// </summary>
            public void WriteArrayStart()
            {
                DoValidation(Condition.NotAProperty);
                PutNewline();

                Put("[");

                _context = new WriterContext
                {
                    InArray = true
                };
                _ctxStack.Push(_context);

                Indent();
            }

            /// <summary>
            /// Ends the current JSON object.
            /// </summary>
            public void WriteObjectEnd()
            {
                DoValidation(Condition.InObject);
                PutNewline(false);

                _ctxStack.Pop();
                if (_ctxStack.Count == 1)
                    _hasReachedEnd = true;
                else
                {
                    _context = _ctxStack.Peek();
                    _context.ExpectingValue = false;
                }

                Unindent();
                Put("}");
            }

            /// <summary>
            /// Starts a new JSON object.
            /// </summary>
            public void WriteObjectStart()
            {
                DoValidation(Condition.NotAProperty);
                PutNewline();

                Put("{");

                _context = new WriterContext
                {
                    InObject = true
                };
                _ctxStack.Push(_context);

                Indent();
            }

            /// <summary>
            /// Writes an object property name and transitions the writer to expect the property value next.
            /// </summary>
            /// <param name="propertyNameVal">Non-null property name.</param>
            /// <remarks>
            /// This method is only valid while writing inside an object and before the corresponding
            /// property value has been written.
            /// </remarks>
            public void WritePropertyName(string propertyNameVal)
            {
                if (propertyNameVal == null)
                    throw new ArgumentNullException(nameof(propertyNameVal));

                DoValidation(Condition.Property);
                PutNewline();
                var propertyName = !_lowerCaseProperties
                    ? propertyNameVal
                    : propertyNameVal.ToLowerInvariant();

                PutString(propertyName);

                if (_prettyPrint)
                {
                    if (propertyName.Length > _context.Padding)
                        _context.Padding = propertyName.Length;

                    for (var i = _context.Padding - propertyName.Length;
                         i >= 0;
                         i--)
                        _writer.Write(' ');

                    _writer.Write(": ");
                }
                else
                    _writer.Write(':');

                _context.ExpectingValue = true;
            }
        }

        #endregion

        #region Lexer.g.cs

        internal class FsmContext
        {
            public bool Return;
            public int NextState;
            public Lexer L = null!;
            public int StateStack;
        }


        internal class Lexer
        {
            #region Fields

            private delegate bool StateHandler(FsmContext ctx);

            private static readonly int[] FsmReturnTable;
            private static readonly StateHandler[] FsmHandlerTable;

            private bool _allowComments;
            private bool _allowSingleQuotedStrings;
            private bool _endOfInput;
            private readonly FsmContext _fsmContext;
            private int _inputBuffer;
            private int _inputChar;
            private readonly TextReader _reader;
            private int _state;
            private readonly StringBuilder _stringBuffer;
            private string _stringValue;
            private int _token;
            private int _unichar;

            #endregion


            #region Properties

            public bool AllowComments
            {
                get => _allowComments;
                set => _allowComments = value;
            }

            public bool AllowSingleQuotedStrings
            {
                get => _allowSingleQuotedStrings;
                set => _allowSingleQuotedStrings = value;
            }

            public bool EndOfInput => _endOfInput;

            public int Token => _token;

            public string StringValue => _stringValue;

            #endregion


            #region Constructors

            static Lexer()
            {
                PopulateFsmTables(out FsmHandlerTable, out FsmReturnTable);
            }

            public Lexer(TextReader reader)
            {
                _allowComments = true;
                _allowSingleQuotedStrings = true;

                _inputBuffer = 0;
                _stringBuffer = new StringBuilder(128);
                _stringValue = string.Empty;
                _state = 1;
                _endOfInput = false;
                _reader = reader;

                _fsmContext = new FsmContext
                {
                    L = this
                };
            }

            #endregion


            #region Static Methods

            private static int HexValue(int digit)
            {
                return digit switch
                {
                    'a' or 'A' => 10,
                    'b' or 'B' => 11,
                    'c' or 'C' => 12,
                    'd' or 'D' => 13,
                    'e' or 'E' => 14,
                    'f' or 'F' => 15,
                    _ => digit - '0'
                };
            }

            private static void PopulateFsmTables(out StateHandler[] fsmHandlerTable, out int[] fsmReturnTable)
            {
                // See section A.1. of the manual for details of the finite
                // state machine.
                fsmHandlerTable = new StateHandler[]
                {
                    State1,
                    State2,
                    State3,
                    State4,
                    State5,
                    State6,
                    State7,
                    State8,
                    State9,
                    State10,
                    State11,
                    State12,
                    State13,
                    State14,
                    State15,
                    State16,
                    State17,
                    State18,
                    State19,
                    State20,
                    State21,
                    State22,
                    State23,
                    State24,
                    State25,
                    State26,
                    State27,
                    State28
                };

                fsmReturnTable = new[]
                {
                    (int)ParserToken.Char,
                    0,
                    (int)ParserToken.Number,
                    (int)ParserToken.Number,
                    0,
                    (int)ParserToken.Number,
                    0,
                    (int)ParserToken.Number,
                    0,
                    0,
                    (int)ParserToken.True,
                    0,
                    0,
                    0,
                    (int)ParserToken.False,
                    0,
                    0,
                    (int)ParserToken.Null,
                    (int)ParserToken.CharSeq,
                    (int)ParserToken.Char,
                    0,
                    0,
                    (int)ParserToken.CharSeq,
                    (int)ParserToken.Char,
                    0,
                    0,
                    0,
                    0
                };
            }

            private static char ProcessEscChar(int escChar)
            {
                return escChar switch
                {
                    '"' or '\'' or '\\' or '/' => Convert.ToChar(escChar),
                    'n' => '\n',
                    't' => '\t',
                    'r' => '\r',
                    'b' => '\b',
                    'f' => '\f',
                    _ => '?'
                };
            }

            private static bool State1(FsmContext ctx)
            {
                while (ctx.L.GetChar())
                {
                    if (ctx.L._inputChar == ' ' ||
                        ctx.L._inputChar >= '\t' && ctx.L._inputChar <= '\r')
                        continue;

                    if (ctx.L._inputChar >= '1' && ctx.L._inputChar <= '9')
                    {
                        ctx.L._stringBuffer.Append((char)ctx.L._inputChar);
                        ctx.NextState = 3;
                        return true;
                    }

                    switch (ctx.L._inputChar)
                    {
                        case '"':
                            ctx.NextState = 19;
                            ctx.Return = true;
                            return true;

                        case ',':
                        case ':':
                        case '[':
                        case ']':
                        case '{':
                        case '}':
                            ctx.NextState = 1;
                            ctx.Return = true;
                            return true;

                        case '-':
                            ctx.L._stringBuffer.Append((char)ctx.L._inputChar);
                            ctx.NextState = 2;
                            return true;

                        case '0':
                            ctx.L._stringBuffer.Append((char)ctx.L._inputChar);
                            ctx.NextState = 4;
                            return true;

                        case 'f':
                            ctx.NextState = 12;
                            return true;

                        case 'n':
                            ctx.NextState = 16;
                            return true;

                        case 't':
                            ctx.NextState = 9;
                            return true;

                        case '\'':
                            if (!ctx.L._allowSingleQuotedStrings)
                                return false;

                            ctx.L._inputChar = '"';
                            ctx.NextState = 23;
                            ctx.Return = true;
                            return true;

                        case '/':
                            if (!ctx.L._allowComments)
                                return false;

                            ctx.NextState = 25;
                            return true;

                        default:
                            return false;
                    }
                }

                return true;
            }

            private static bool State2(FsmContext ctx)
            {
                ctx.L.GetChar();

                if (ctx.L._inputChar >= '1' && ctx.L._inputChar <= '9')
                {
                    ctx.L._stringBuffer.Append((char)ctx.L._inputChar);
                    ctx.NextState = 3;
                    return true;
                }

                switch (ctx.L._inputChar)
                {
                    case '0':
                        ctx.L._stringBuffer.Append((char)ctx.L._inputChar);
                        ctx.NextState = 4;
                        return true;

                    default:
                        return false;
                }
            }

            private static bool State3(FsmContext ctx)
            {
                while (ctx.L.GetChar())
                {
                    if (ctx.L._inputChar >= '0' && ctx.L._inputChar <= '9')
                    {
                        ctx.L._stringBuffer.Append((char)ctx.L._inputChar);
                        continue;
                    }

                    if (ctx.L._inputChar == ' ' ||
                        ctx.L._inputChar >= '\t' && ctx.L._inputChar <= '\r')
                    {
                        ctx.Return = true;
                        ctx.NextState = 1;
                        return true;
                    }

                    switch (ctx.L._inputChar)
                    {
                        case ',':
                        case ']':
                        case '}':
                            ctx.L.UngetChar();
                            ctx.Return = true;
                            ctx.NextState = 1;
                            return true;

                        case '.':
                            ctx.L._stringBuffer.Append((char)ctx.L._inputChar);
                            ctx.NextState = 5;
                            return true;

                        case 'e':
                        case 'E':
                            ctx.L._stringBuffer.Append((char)ctx.L._inputChar);
                            ctx.NextState = 7;
                            return true;

                        default:
                            return false;
                    }
                }

                return true;
            }

            private static bool State4(FsmContext ctx)
            {
                ctx.L.GetChar();

                if (ctx.L._inputChar == ' ' ||
                    ctx.L._inputChar >= '\t' && ctx.L._inputChar <= '\r')
                {
                    ctx.Return = true;
                    ctx.NextState = 1;
                    return true;
                }

                switch (ctx.L._inputChar)
                {
                    case ',':
                    case ']':
                    case '}':
                        ctx.L.UngetChar();
                        ctx.Return = true;
                        ctx.NextState = 1;
                        return true;

                    case '.':
                        ctx.L._stringBuffer.Append((char)ctx.L._inputChar);
                        ctx.NextState = 5;
                        return true;

                    case 'e':
                    case 'E':
                        ctx.L._stringBuffer.Append((char)ctx.L._inputChar);
                        ctx.NextState = 7;
                        return true;

                    default:
                        return false;
                }
            }

            private static bool State5(FsmContext ctx)
            {
                ctx.L.GetChar();

                if (ctx.L._inputChar >= '0' && ctx.L._inputChar <= '9')
                {
                    ctx.L._stringBuffer.Append((char)ctx.L._inputChar);
                    ctx.NextState = 6;
                    return true;
                }

                return false;
            }

            private static bool State6(FsmContext ctx)
            {
                while (ctx.L.GetChar())
                {
                    if (ctx.L._inputChar >= '0' && ctx.L._inputChar <= '9')
                    {
                        ctx.L._stringBuffer.Append((char)ctx.L._inputChar);
                        continue;
                    }

                    if (ctx.L._inputChar == ' ' ||
                        ctx.L._inputChar >= '\t' && ctx.L._inputChar <= '\r')
                    {
                        ctx.Return = true;
                        ctx.NextState = 1;
                        return true;
                    }

                    switch (ctx.L._inputChar)
                    {
                        case ',':
                        case ']':
                        case '}':
                            ctx.L.UngetChar();
                            ctx.Return = true;
                            ctx.NextState = 1;
                            return true;

                        case 'e':
                        case 'E':
                            ctx.L._stringBuffer.Append((char)ctx.L._inputChar);
                            ctx.NextState = 7;
                            return true;

                        default:
                            return false;
                    }
                }

                return true;
            }

            private static bool State7(FsmContext ctx)
            {
                ctx.L.GetChar();

                if (ctx.L._inputChar >= '0' && ctx.L._inputChar <= '9')
                {
                    ctx.L._stringBuffer.Append((char)ctx.L._inputChar);
                    ctx.NextState = 8;
                    return true;
                }

                switch (ctx.L._inputChar)
                {
                    case '+':
                    case '-':
                        ctx.L._stringBuffer.Append((char)ctx.L._inputChar);
                        ctx.NextState = 8;
                        return true;

                    default:
                        return false;
                }
            }

            private static bool State8(FsmContext ctx)
            {
                while (ctx.L.GetChar())
                {
                    if (ctx.L._inputChar >= '0' && ctx.L._inputChar <= '9')
                    {
                        ctx.L._stringBuffer.Append((char)ctx.L._inputChar);
                        continue;
                    }

                    if (ctx.L._inputChar == ' ' ||
                        ctx.L._inputChar >= '\t' && ctx.L._inputChar <= '\r')
                    {
                        ctx.Return = true;
                        ctx.NextState = 1;
                        return true;
                    }

                    switch (ctx.L._inputChar)
                    {
                        case ',':
                        case ']':
                        case '}':
                            ctx.L.UngetChar();
                            ctx.Return = true;
                            ctx.NextState = 1;
                            return true;

                        default:
                            return false;
                    }
                }

                return true;
            }

            private static bool State9(FsmContext ctx)
            {
                ctx.L.GetChar();

                switch (ctx.L._inputChar)
                {
                    case 'r':
                        ctx.NextState = 10;
                        return true;

                    default:
                        return false;
                }
            }

            private static bool State10(FsmContext ctx)
            {
                ctx.L.GetChar();

                switch (ctx.L._inputChar)
                {
                    case 'u':
                        ctx.NextState = 11;
                        return true;

                    default:
                        return false;
                }
            }

            private static bool State11(FsmContext ctx)
            {
                ctx.L.GetChar();

                switch (ctx.L._inputChar)
                {
                    case 'e':
                        ctx.Return = true;
                        ctx.NextState = 1;
                        return true;

                    default:
                        return false;
                }
            }

            private static bool State12(FsmContext ctx)
            {
                ctx.L.GetChar();

                switch (ctx.L._inputChar)
                {
                    case 'a':
                        ctx.NextState = 13;
                        return true;

                    default:
                        return false;
                }
            }

            private static bool State13(FsmContext ctx)
            {
                ctx.L.GetChar();

                switch (ctx.L._inputChar)
                {
                    case 'l':
                        ctx.NextState = 14;
                        return true;

                    default:
                        return false;
                }
            }

            private static bool State14(FsmContext ctx)
            {
                ctx.L.GetChar();

                switch (ctx.L._inputChar)
                {
                    case 's':
                        ctx.NextState = 15;
                        return true;

                    default:
                        return false;
                }
            }

            private static bool State15(FsmContext ctx)
            {
                ctx.L.GetChar();

                switch (ctx.L._inputChar)
                {
                    case 'e':
                        ctx.Return = true;
                        ctx.NextState = 1;
                        return true;

                    default:
                        return false;
                }
            }

            private static bool State16(FsmContext ctx)
            {
                ctx.L.GetChar();

                switch (ctx.L._inputChar)
                {
                    case 'u':
                        ctx.NextState = 17;
                        return true;

                    default:
                        return false;
                }
            }

            private static bool State17(FsmContext ctx)
            {
                ctx.L.GetChar();

                switch (ctx.L._inputChar)
                {
                    case 'l':
                        ctx.NextState = 18;
                        return true;

                    default:
                        return false;
                }
            }

            private static bool State18(FsmContext ctx)
            {
                ctx.L.GetChar();

                switch (ctx.L._inputChar)
                {
                    case 'l':
                        ctx.Return = true;
                        ctx.NextState = 1;
                        return true;

                    default:
                        return false;
                }
            }

            private static bool State19(FsmContext ctx)
            {
                while (ctx.L.GetChar())
                {
                    switch (ctx.L._inputChar)
                    {
                        case '"':
                            ctx.L.UngetChar();
                            ctx.Return = true;
                            ctx.NextState = 20;
                            return true;

                        case '\\':
                            ctx.StateStack = 19;
                            ctx.NextState = 21;
                            return true;

                        default:
                            ctx.L._stringBuffer.Append((char)ctx.L._inputChar);
                            continue;
                    }
                }

                return true;
            }

            private static bool State20(FsmContext ctx)
            {
                ctx.L.GetChar();

                switch (ctx.L._inputChar)
                {
                    case '"':
                        ctx.Return = true;
                        ctx.NextState = 1;
                        return true;

                    default:
                        return false;
                }
            }

            private static bool State21(FsmContext ctx)
            {
                ctx.L.GetChar();

                switch (ctx.L._inputChar)
                {
                    case 'u':
                        ctx.NextState = 22;
                        return true;

                    case '"':
                    case '\'':
                    case '/':
                    case '\\':
                    case 'b':
                    case 'f':
                    case 'n':
                    case 'r':
                    case 't':
                        ctx.L._stringBuffer.Append(
                            ProcessEscChar(ctx.L._inputChar));
                        ctx.NextState = ctx.StateStack;
                        return true;

                    default:
                        return false;
                }
            }

            private static bool State22(FsmContext ctx)
            {
                var counter = 0;
                var mult = 4096;

                ctx.L._unichar = 0;

                while (ctx.L.GetChar())
                {
                    if (ctx.L._inputChar >= '0' && ctx.L._inputChar <= '9' ||
                        ctx.L._inputChar >= 'A' && ctx.L._inputChar <= 'F' ||
                        ctx.L._inputChar >= 'a' && ctx.L._inputChar <= 'f')
                    {
                        ctx.L._unichar += HexValue(ctx.L._inputChar) * mult;

                        counter++;
                        mult /= 16;

                        if (counter == 4)
                        {
                            ctx.L._stringBuffer.Append(
                                Convert.ToChar(ctx.L._unichar));
                            ctx.NextState = ctx.StateStack;
                            return true;
                        }

                        continue;
                    }

                    return false;
                }

                return true;
            }

            private static bool State23(FsmContext ctx)
            {
                while (ctx.L.GetChar())
                {
                    switch (ctx.L._inputChar)
                    {
                        case '\'':
                            ctx.L.UngetChar();
                            ctx.Return = true;
                            ctx.NextState = 24;
                            return true;

                        case '\\':
                            ctx.StateStack = 23;
                            ctx.NextState = 21;
                            return true;

                        default:
                            ctx.L._stringBuffer.Append((char)ctx.L._inputChar);
                            continue;
                    }
                }

                return true;
            }

            private static bool State24(FsmContext ctx)
            {
                ctx.L.GetChar();

                switch (ctx.L._inputChar)
                {
                    case '\'':
                        ctx.L._inputChar = '"';
                        ctx.Return = true;
                        ctx.NextState = 1;
                        return true;

                    default:
                        return false;
                }
            }

            private static bool State25(FsmContext ctx)
            {
                ctx.L.GetChar();

                switch (ctx.L._inputChar)
                {
                    case '*':
                        ctx.NextState = 27;
                        return true;

                    case '/':
                        ctx.NextState = 26;
                        return true;

                    default:
                        return false;
                }
            }

            private static bool State26(FsmContext ctx)
            {
                while (ctx.L.GetChar())
                {
                    if (ctx.L._inputChar == '\n')
                    {
                        ctx.NextState = 1;
                        return true;
                    }
                }

                return true;
            }

            private static bool State27(FsmContext ctx)
            {
                while (ctx.L.GetChar())
                {
                    if (ctx.L._inputChar == '*')
                    {
                        ctx.NextState = 28;
                        return true;
                    }
                }

                return true;
            }

            private static bool State28(FsmContext ctx)
            {
                while (ctx.L.GetChar())
                {
                    if (ctx.L._inputChar == '*')
                        continue;

                    if (ctx.L._inputChar == '/')
                    {
                        ctx.NextState = 1;
                        return true;
                    }

                    ctx.NextState = 27;
                    return true;
                }

                return true;
            }

            #endregion


            private bool GetChar()
            {
                if ((_inputChar = NextChar()) != -1)
                    return true;

                _endOfInput = true;
                return false;
            }

            private int NextChar()
            {
                if (_inputBuffer != 0)
                {
                    var tmp = _inputBuffer;
                    _inputBuffer = 0;

                    return tmp;
                }

                return _reader.Read();
            }

            public bool NextToken()
            {
                StateHandler handler;
                _fsmContext.Return = false;

                while (true)
                {
                    handler = FsmHandlerTable[_state - 1];

                    if (!handler(_fsmContext))
                        throw new JsonException(_inputChar);

                    if (_endOfInput)
                        return false;

                    if (_fsmContext.Return)
                    {
                        _stringValue = _stringBuffer.ToString();
                        _stringBuffer.Remove(0, _stringBuffer.Length);
                        _token = FsmReturnTable[_state - 1];

                        if (_token == (int)ParserToken.Char)
                            _token = _inputChar;

                        _state = _fsmContext.NextState;

                        return true;
                    }

                    _state = _fsmContext.NextState;
                }
            }

            private void UngetChar()
            {
                _inputBuffer = _inputChar;
            }
        }

        #endregion

        #region Netstandard15Polyfill.g.cs

#if NETSTANDARD1_5
    internal static class Netstandard15Polyfill
    {
        internal static Type GetInterface(this Type type, string name)
        {
            return type.GetTypeInfo().GetInterface(name); 
        }

        internal static bool IsClass(this Type type)
        {
            return type.GetTypeInfo().IsClass;
        }

        internal static bool IsEnum(this Type type)
        {
            return type.GetTypeInfo().IsEnum;
        }
    }
#endif

        #endregion

        #region ParserToken.g.cs

        internal enum ParserToken
        {
            // Lexer tokens (see section A.1.1. of the manual)
            None = char.MaxValue + 1,
            Number,
            True,
            False,
            Null,
            CharSeq,

            // Single char
            Char,

            // Parser Rules (see section A.2.1 of the manual)
            Text,
            Object,
            ObjectPrime,
            Pair,
            PairRest,
            Array,
            ArrayPrime,
            Value,
            ValueRest,
            String,

            // End of input
            End,

            // The empty rule
            Epsilon
        }

        #endregion
    }
}
