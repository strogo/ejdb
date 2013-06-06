// ============================================================================================
//   .NET API for EJDB database library http://ejdb.org
//   Copyright (C) 2012-2013 Softmotions Ltd <info@softmotions.com>
//
//   This file is part of EJDB.
//   EJDB is free software; you can redistribute it and/or modify it under the terms of
//   the GNU Lesser General Public License as published by the Free Software Foundation; either
//   version 2.1 of the License or any later version.  EJDB is distributed in the hope
//   that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
//   MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU Lesser General Public
//   License for more details.
//   You should have received a copy of the GNU Lesser General Public License along with EJDB;
//   if not, write to the Free Software Foundation, Inc., 59 Temple Place, Suite 330,
//   Boston, MA 02111-1307 USA.
// ============================================================================================
using System;
using System.IO;
using System.Collections.Generic;
using System.Collections;
using System.Text;
using System.Diagnostics;

namespace Ejdb.SON {

	/**
	 * <summary>BSON field value</summary>
	 **/
	[Serializable]
	public sealed class BSONValue {
		public readonly BSONType Type;
		public readonly string Key;
		public readonly object Value;

		public BSONValue(BSONType type, string key, object value) {
			this.Type = type;
			this.Key = key;
			this.Value = value;
		}

		public BSONValue(BSONType type, string key) : this(type, key, null) {
		}
	}

	/**
	 * <summary>BSON document deserialized data wrapper</summary>
	 **/
	[Serializable]
	public sealed class BSONDocument {
		Dictionary<string, BSONValue> _fields;
		readonly List<BSONValue> _fieldslist;

		public BSONDocument() {
			this._fields = null;
			this._fieldslist = new List<BSONValue>();
		}

		public BSONDocument Add(BSONValue bv) {
			_fieldslist.Add(bv);
			if (_fields != null) {
				_fields.Add(bv.Key, bv);
			}
			return this;
		}

		public BSONValue GetBSONValue(string key) {
			CheckFields();
			return _fields[key];
		}

		void CheckFields() {
			if (_fields != null) {
				return;
			}
			_fields = new Dictionary<string, BSONValue>(Math.Max(_fieldslist.Count + 1, 32));
			foreach (var bv in _fieldslist) {
				_fields.Add(bv.Key, bv);
			}
		}
	}

	public class BSONIterator : IDisposable {

		BinaryReader _input;
		int _doclen;
		BSONType _ctype = BSONType.UNKNOWN;
		string _entryKey;
		int _entryLen;
		bool _entryDataSkipped;
		BSONValue _entryDataValue;

		public int DocumentLength {
			get { return this._doclen; }
			private set { this._doclen = value; }
		}

		public BSONIterator(byte[] bbuf) : this(new MemoryStream(bbuf)) {
		}

		public BSONIterator(Stream input) {
			if (!input.CanRead) {
				Dispose();
				throw new IOException("Input stream must be readable");
			}
			if (!input.CanSeek) {
				Dispose();
				throw new IOException("Input stream must be seekable");
			}
			this._input = new BinaryReader(input, Encoding.UTF8);
			this._ctype = BSONType.EOO;
			this._doclen = _input.ReadInt32();
			if (this._doclen < 5) {
				Dispose();
				throw new InvalidBSONDataException("Unexpected end of BSON document");
			}
		}

		~BSONIterator() {
			Dispose();
		}

		public void Dispose() {
			if (_input != null) {
				_input.Close();
				_input = null;
			}
		}

		public BSONType Next() {
			if (_ctype == BSONType.EOO) {
				return BSONType.EOO;
			}
			if (_ctype != BSONType.UNKNOWN) {
				SkipData();
			}
			byte bv = _input.ReadByte();
			if (!Enum.IsDefined(typeof(BSONType), bv)) {
				throw new InvalidBSONDataException("Unknown bson type: " + bv);
			}
			_entryDataSkipped = false;
			_entryDataValue = null;
			_entryKey = null;
			_ctype = (BSONType) bv;
			_entryLen = 0;
			if (_ctype != BSONType.EOO) {
				ReadKey();
			}
			switch (_ctype) {
				case BSONType.EOO:
					return BSONType.EOO;
				case BSONType.UNDEFINED:
				case BSONType.NULL:
					_entryLen = 0;
					break;
				case BSONType.BOOL:
					_entryLen = 1;
					break;
				case BSONType.INT:
					_entryLen = 4;
					break;
				case BSONType.LONG:
				case BSONType.DOUBLE:
				case BSONType.TIMESTAMP:
				case BSONType.DATE:
					_entryLen = 8;
					break;
				case BSONType.OID:
					_entryLen = 12;
					break;	
				case BSONType.STRING:
				case BSONType.CODE:
				case BSONType.SYMBOL:
					_entryLen = 4 + _input.ReadInt32();
					break;
				case BSONType.DBREF:
					_entryLen = 4 + 12 + _input.ReadInt32();
					break;
				case BSONType.BINDATA:
					_entryLen = 4 + 1 + _input.ReadInt32();
					break;
				case BSONType.OBJECT:
				case BSONType.ARRAY:
				case BSONType.CODEWSCOPE:
					_entryLen = 4 + _input.ReadInt32(); 
					break;
				case BSONType.REGEX:
					_entryLen = 0;
					break;
				default:
					throw new InvalidBSONDataException("Unknown entry type: " + _ctype);
			}
			return _ctype;
		}

		public BSONValue FetchIteratorValue() {
			if (_entryDataSkipped) {
				return _entryDataValue;
			}
			_entryDataSkipped = true;
			switch (_ctype) {
				case BSONType.EOO:					 
				case BSONType.UNDEFINED:
				case BSONType.NULL:
					_entryDataValue = new BSONValue(_ctype, _entryKey);
					break;	
				case BSONType.BOOL:
					break;

			}
			return _entryDataValue;
		}

		void SkipData() {
			if (_entryDataSkipped) {
				return;
			}
			_entryDataValue = null;
			_entryDataSkipped = true;
			if (_ctype == BSONType.REGEX) {
				SkipCString();
				SkipCString();
				Debug.Assert(_entryLen == 0);
			} else if (_entryLen > 0) {
				long cpos = _input.BaseStream.Position;
				if ((cpos + _entryLen) != _input.BaseStream.Seek(_entryLen, SeekOrigin.Current)) {
					throw new IOException("Inconsitent seek within input BSON stream");
				}
				_entryLen = 0;
			} 
		}

		string ReadKey() {
			_entryKey = ReadCString();
			return _entryKey;
		}

		string ReadCString() {
			List<byte> sb = new List<byte>(64);
			byte bv;
			while ((bv = _input.ReadByte()) != 0x00) {
				sb.Add(bv);
			}
			return Encoding.UTF8.GetString(sb.ToArray());
		}

		void SkipCString() {
			while ((_input.ReadByte()) != 0x00)
				;
		}
	}
}
