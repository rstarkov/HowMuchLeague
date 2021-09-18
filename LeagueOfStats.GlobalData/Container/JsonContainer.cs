using System;
using System.Collections.Generic;
using System.IO;
using RT.Util.ExtensionMethods;
using RT.Json;

namespace LeagueOfStats.GlobalData
{
    [DataTypeId("JSON")]
    public class JsonContainer : LosContainer<JsonValue, JsonContainer.ChunkState>
    {
        public JsonContainer(string filename) : base(filename)
        {
        }

        protected override LosContainer<JsonValue, ChunkState> Clone(string filename)
        {
            return new JsonContainer(filename);
        }

        protected override byte[] SerializeFormatSpecificData()
        {
            return new byte[0];
        }

        public class ChunkState
        {
            public List<string> PropNames = new List<string>();
            public Dictionary<string, int> PropNameIds = new Dictionary<string, int>();
        }

        protected override ChunkState GetInitialChunkState()
        {
            return new ChunkState();
        }

        protected override byte WriteItem(Stream stream, JsonValue item, ChunkState state)
        {
            writeJsonValue(stream, item, state);
            return 0; // item format version
        }

        private const byte _stringCodeFirst = 6;
        private const byte _dictCodeFirst = 89;
        private const byte _listCodeFirst = 172;
        private const byte _stringCodeLast = _dictCodeFirst - 1;
        private const byte _dictCodeLast = _listCodeFirst - 1;
        private const byte _listCodeLast = 254; // 255 reserved for future additions

        private void writeJsonValue(Stream stream, JsonValue item, ChunkState state)
        {
            if (item == null)
            {
                stream.WriteByte(0);
            }
            else if (item is JsonBool)
            {
                if (!item.GetBool())
                    stream.WriteByte(1);
                else
                    stream.WriteByte(2);
            }
            else if (item is JsonNumber jsonNumber)
            {
                var raw = jsonNumber.RawValue;
                if (raw is long numL)
                {
                    stream.WriteByte(3);
                    stream.WriteInt64Optim(numL);
                }
                else if (raw is ulong numUL)
                {
                    stream.WriteByte(4);
                    stream.WriteUInt64Optim(numUL);
                }
                else if (raw is double numD)
                {
                    stream.WriteByte(5);
                    stream.Write(BitConverter.GetBytes(numD));
                }
            }
            else if (item is JsonString)
            {
                var bytes = item.GetString().ToUtf8();
                writeLength(stream, bytes.Length, _stringCodeFirst, _stringCodeLast);
                stream.Write(bytes);
            }
            else if (item is JsonList jsonList)
            {
                writeLength(stream, jsonList.Count, _listCodeFirst, _listCodeLast);
                foreach (var val in jsonList)
                    writeJsonValue(stream, val, state);
            }
            else if (item is JsonDict jsonDict)
            {
                writeLength(stream, jsonDict.Count, _dictCodeFirst, _dictCodeLast);
                foreach (var kvp in jsonDict)
                {
                    // Key
                    if (state.PropNameIds.TryGetValue(kvp.Key, out int keyId))
                    {
                        stream.WriteByte(255);
                        stream.WriteUInt32Optim((uint) keyId);
                    }
                    else
                    {
                        state.PropNameIds.Add(kvp.Key, state.PropNameIds.Count);
                        // state.PropNames.Add(kvp.Key); - only needed for reading, which never shares state with writing so might as well not bother to update this one
                        var bytes = kvp.Key.ToUtf8();
                        writeLength(stream, bytes.Length, 0, 254);
                        stream.Write(bytes);
                    }
                    // Value
                    writeJsonValue(stream, kvp.Value, state);
                }
            }
            else
                throw new Exception();
        }

        private void writeLength(Stream stream, int length, byte codeFirst, byte codeLast)
        {
            byte count = (byte) (codeLast - codeFirst + 1);
            if (length < count - 1)
                stream.WriteByte((byte) (codeFirst + length));
            else
            {
                stream.WriteByte(codeLast);
                stream.WriteUInt32Optim((uint) length);
            }
        }

        protected override JsonValue ReadItem(Stream stream, byte itemFormatVersion, ChunkState state)
        {
            if (itemFormatVersion == 0)
                return readJsonValueFormat0(stream, state);
            else
                throw new Exception($"Unsupported item format version: {itemFormatVersion}");
        }

        private JsonValue readJsonValueFormat0(Stream stream, ChunkState state)
        {
            byte type = (byte) stream.ReadByte();

            if (type == 0)
                return null;
            else if (type == 1)
                return false;
            else if (type == 2)
                return true;
            else if (type == 3)
                return stream.ReadInt64Optim();
            else if (type == 4)
                return stream.ReadUInt64Optim();
            else if (type == 5)
                return BitConverter.ToDouble(stream.Read(8), 0);
            else if (type >= _stringCodeFirst && type <= _stringCodeLast)
            {
                var str = stream.Read(readLength(stream, type, _stringCodeFirst, _stringCodeLast)).FromUtf8();
                return str;
            }
            else if (type >= _listCodeFirst && type <= _listCodeLast)
            {
                var count = readLength(stream, type, _listCodeFirst, _listCodeLast);
                var result = new JsonList();
                for (int i = 0; i < count; i++)
                    result.Add(readJsonValueFormat0(stream, state));
                return result;
            }
            else if (type >= _dictCodeFirst && type <= _dictCodeLast)
            {
                var count = readLength(stream, type, _dictCodeFirst, _dictCodeLast);
                var result = new JsonDict();
                for (int i = 0; i < count; i++)
                {
                    byte keyType = (byte) stream.ReadByte();
                    string key;
                    if (keyType == 255)
                        key = state.PropNames[(int) stream.ReadUInt32Optim()];
                    else
                    {
                        key = stream.Read(readLength(stream, keyType, 0, 254)).FromUtf8();
                        // state.PropNameIds.Add(key, state.PropNameIds.Count); - only needed for writing, which never shares state with reading so might as well not bother to update this one
                        state.PropNames.Add(key);
                    }
                    result.Add(key, readJsonValueFormat0(stream, state));
                }
                return result;
            }
            else
                throw new Exception();
        }

        private int readLength(Stream stream, byte code, byte codeFirst, byte codeLast)
        {
            if (code == codeLast)
                return (int) stream.ReadUInt32Optim();
            else
                return (int) (uint) code - codeFirst; // overflow checking ensures it's non-negative
        }
    }
}
