using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using RT.Util;
using RT.Util.ExtensionMethods;
using RT.Util.Streams;

// Wishlist:
//    Do not create empty files / empty chunks when appending an empty enumerable
//    Allow derived types to determine for themselves whether a rewrite is worth doing
//    Correctly support long chunks instead of throwing an exception

namespace LeagueOfStats.GlobalData
{
    public abstract class LosContainer
    {
        public string FileName { get; private set; }

        public LosContainer(string filename)
        {
            FileName = filename;
        }

        public abstract void Initialise(bool compact = false);

        protected string DataTypeId
        {
            get
            {
                return DataTypeIdAttribute.GetId(GetType());
            }
        }

        public static LosContainer TryLoad(string filename)
        {
            if (!File.Exists(filename))
                return null;

            string datatype;
            using (var stream = File.Open(filename, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var magic = stream.Read(6);
                if (!magic.SequenceEqual("LOSDS-".ToUtf8()))
                    return null;

                datatype = stream.Read(4).FromUtf8();
            }

            if (datatype == DataTypeIdAttribute.GetId(typeof(JsonContainer)))
                return new JsonContainer(filename);
            else if (datatype == DataTypeIdAttribute.GetId(typeof(MatchIdContainer)))
                return new MatchIdContainer(filename);
            else
                throw new Exception();
        }
    }

    // Non-existent files are equivalent to empty files. Created automatically on first append.
    // Reads support all previous file, chunk and item format versions. Appends support all previous file format versions, but only the latest chunk and item format versions.
    public abstract class LosContainer<TItem, TChunkState> : LosContainer where TChunkState : class
    {
        public LosContainer(string filename) : base(filename)
        {
        }

        protected abstract TChunkState GetInitialChunkState();
        protected abstract TItem ReadItem(Stream stream, byte itemFormatVersion, TChunkState state);
        protected abstract byte WriteItem(Stream stream, TItem item, TChunkState state);
        protected abstract LosContainer<TItem, TChunkState> Clone(string filename);
        protected abstract byte[] SerializeFormatSpecificData();

        protected class operationData
        {
            public Stream Stream;
            public BinaryReader Reader;
            public BinaryWriter Writer;

            public byte FileFormatVersion;

            public byte OldestItemFormatVersion;
            public uint ShortChunkCount;
            public uint CompressedItemsCount;
            public uint UncompressedItemsCount;

            public byte[] FormatSpecificData;

            public long ValidLength;
        }

        protected IEnumerable<T> operateOnFile<T>(bool write, Func<operationData, IEnumerable<T>> operation)
        {
            bool newFile = !File.Exists(FileName);
            if (newFile && !write)
            {
                foreach (var item in operation(null))
                    yield return item;
                yield break;
            }

            var op = new operationData();
            if (write)
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(FileName)));
            using (op.Stream = Ut.WaitSharingVio(() => File.Open(FileName, FileMode.OpenOrCreate, write ? FileAccess.ReadWrite : FileAccess.Read, FileShare.Read)))
            {
                op.Reader = new BinaryReader(op.Stream, Encoding.UTF8, leaveOpen: true);
                op.Writer = write ? new BinaryWriter(op.Stream, Encoding.UTF8, leaveOpen: true) : null;

                if (newFile)
                    op.Writer.Write("LOSDS-".ToUtf8());
                else
                {
                    var magic = op.Stream.Read(6);
                    if (!magic.SequenceEqual("LOSDS-".ToUtf8()))
                        throw new NotSupportedException($"Expected bytes \"LOSDS-\" at the start of this file: {FileName}");
                }

                if (newFile)
                {
                    if (DataTypeId.ToUtf8().Length != 4)
                        throw new Exception($"{nameof(DataTypeId)} must be 4 bytes long in UTF-8");
                    op.Writer.Write(DataTypeId.ToUtf8());
                }
                else
                {
                    var datatype = op.Stream.Read(4);
                    if (!datatype.SequenceEqual(DataTypeId.ToUtf8()))
                        throw new NotSupportedException($"Expected data type \"{DataTypeId}\" in file: {FileName}");
                }

                op.FileFormatVersion = (byte) (newFile ? 1 : op.Reader.ReadByte());
                if (newFile)
                    op.Writer.Write(op.FileFormatVersion);

                if (op.FileFormatVersion == 1)
                {
                    var statsPos = op.Stream.Position;
                    op.OldestItemFormatVersion = (byte) (newFile ? 0 : op.Reader.ReadByte());
                    if (newFile)
                        op.Writer.Write(op.OldestItemFormatVersion);
                    op.ShortChunkCount = newFile ? 0 : op.Reader.ReadUInt32();
                    if (newFile)
                        op.Writer.Write(op.ShortChunkCount);
                    op.CompressedItemsCount = newFile ? 0 : op.Reader.ReadUInt32();
                    if (newFile)
                        op.Writer.Write(op.CompressedItemsCount);
                    op.UncompressedItemsCount = newFile ? 0 : op.Reader.ReadUInt32();
                    if (newFile)
                        op.Writer.Write(op.UncompressedItemsCount);

                    if (newFile)
                    {
                        var data = SerializeFormatSpecificData();
                        if (data.Length > 254) // 255 reserved for encoding longer sections if ever required
                            throw new Exception();
                        op.Writer.Write((byte) data.Length);
                        op.Writer.Write(data);
                    }
                    else
                        op.FormatSpecificData = op.Reader.ReadBytes(op.Reader.ReadByte());
                    if (newFile)
                    {
                        op.Writer.Write((byte) 0); // 3 bytes reserved for future expansion
                        op.Writer.Write((ushort) 0);
                    }
                    else
                        op.Reader.ReadBytes(3);

                    var validLengthPos = op.Stream.Position;
                    op.ValidLength = newFile ? (validLengthPos + 8) : op.Reader.ReadInt64();
                    if (newFile)
                        op.Writer.Write(op.ValidLength);

                    if (write)
                        op.Stream.Position = op.ValidLength;

                    foreach (var item in operation(op))
                        yield return item;

                    if (write)
                    {
                        // Commit this write
                        if (op.Stream.Length > op.ValidLength)
                            op.Stream.SetLength(op.ValidLength);
                        op.Stream.Position = validLengthPos;
                        op.Writer.Write(op.ValidLength);
                        // Update stats
                        op.Stream.Position = statsPos;
                        op.Writer.Write(op.OldestItemFormatVersion);
                        op.Writer.Write(op.ShortChunkCount);
                        op.Writer.Write(op.CompressedItemsCount);
                        op.Writer.Write(op.UncompressedItemsCount);
                    }
                }
                else
                    throw new NotSupportedException($"LOSDS version {op.FileFormatVersion} is not supported: {FileName}");
            }
        }

        /// <summary>
        ///     Verifies the file is in the correct format. Compacts the file based on stats.</summary>
        /// <param name="compact">
        ///     If true, the file will be compacted as long as there is anything at all to compact, ignoring the heuristics
        ///     for whether the savings will be significant enough.</param>
        public override void Initialise(bool compact = false)
        {
            operationData stats = null;
            operateOnFile(write: false, operation: op =>
            {
                stats = op;
                return Enumerable.Empty<bool>();
            }).ToList();
            if (stats == null)
                return;

            if (compact && (stats.ShortChunkCount > 1 || stats.UncompressedItemsCount > 0))
                Rewrite();
            else if (stats.ShortChunkCount > 2000 || stats.UncompressedItemsCount > 5000)
                Rewrite();
        }

        /// <summary>
        ///     Reads and re-writes the entire file, updating the file and item formats to the latest versions, compressing
        ///     all chunks and minimising the chunk count.</summary>
        public virtual void Rewrite(Func<IEnumerable<TItem>, IEnumerable<TItem>> filter = null)
        {
            if (!File.Exists(FileName))
                return;
            var tempName = FileName + ".rewrite";
            if (File.Exists(tempName))
                File.Delete(tempName);
            var prevSize = new FileInfo(FileName).Length;
            var source = Clone(FileName);
            var dest = Clone(tempName);
            var count = new CountResult();
            if (filter == null)
                dest.AppendItems(source.ReadItems().PassthroughCount(count), compressed: true);
            else
                dest.AppendItems(filter(source.ReadItems()).PassthroughCount(count), compressed: true);
            var newSize = new FileInfo(tempName).Length;
            if (newSize < prevSize / 15)
                throw new Exception("Buggy rewrite?");
            File.Delete(FileName);
            File.Move(tempName, FileName);
            Console.WriteLine($"Rewritten from {prevSize:#,0} to {newSize:#,0} ({count.Count:#,0} items): {FileName}");
        }

        /// <summary>Reads all items from the file.</summary>
        public IEnumerable<TItem> ReadItems()
        {
            return operateOnFile(write: false, operation: readCore);
        }

        private IEnumerable<TItem> readCore(operationData op)
        {
            if (op == null)
                yield break;
            while (op.Stream.Position < op.ValidLength)
            {
                var scheme = op.Reader.ReadByte();
                if (scheme == 1)
                {
                    var itemFormatVersion = op.Reader.ReadByte();
                    var chunkLength = (long) op.Reader.ReadUInt32(); // must be fixed length as it's patched in at the end of a write.
                    uint crc32;
                    using (var windowStream = new WindowStream(op.Stream, chunkLength))
                    using (var decompressed = new DeflateStream(windowStream, CompressionMode.Decompress))
                    {
                        var crc32stream = new CRC32Stream(decompressed);
                        var endstream = new EndDetectionStream(crc32stream);
                        var state = GetInitialChunkState();
                        while (!endstream.IsEnded)
                        {
                            var length = endstream.ReadUInt32Optim();
                            using (var windowInner = new WindowStream(endstream, length))
                                yield return ReadItem(windowInner, itemFormatVersion, state);
                        }
                        crc32 = crc32stream.CRC;
                    }
                    if (op.Reader.ReadUInt32() != crc32)
                        throw new Exception($"CRC32 check failed: {FileName}");
                }
                else if (scheme == 2)
                {
                    var itemFormatVersion = op.Reader.ReadByte();
                    var chunkLength = (long) op.Stream.ReadUInt64Optim(); // ulong is very slightly more compact than long
                    using (var windowStream = new WindowStream(op.Stream, chunkLength))
                        yield return ReadItem(windowStream, itemFormatVersion, GetInitialChunkState());
                }
                else
                    throw new NotSupportedException($"LOSDS scheme {scheme} is not supported: {FileName}");
            }
        }

        /// <summary>
        ///     Appends the specified items to the file, by appending a new chunk. See Remarks.</summary>
        /// <param name="items">
        ///     Items to append.</param>
        /// <param name="compressed">
        ///     Whether to use a compressed or an uncompressed chunk. See Remarks.</param>
        /// <remarks>
        ///     <para>
        ///         When heuristics indicate that a rewrite would be beneficial, the file is automatically rewritten after the
        ///         append, which means that this operation might take a long time.</para>
        ///     <para>
        ///         Compressed chunks have a certain overhead, and it might not be worth using a compressed chunk when
        ///         appending a single item.</para></remarks>
        public void AppendItems(IEnumerable<TItem> items, bool compressed)
        {
            bool rewriteNeeded = false;
            operateOnFile(write: true, operation: op =>
            {
                if (compressed)
                {
                    op.Writer.Write((byte) 1); // chunk type - compressed
                    var memoryStream = new MemoryStream(); // reuse it to avoid constant array reallocation
                    var memoryWriter = new BinaryWriter(memoryStream);
                    op.Writer.Write((byte) 0); // item format version - patched in later
                    op.Writer.Write((uint) 0); // chunk length - patched in later
                    var chunkStartPos = op.Stream.Position;
                    int itemFormatAll = -1;
                    uint crc32;
                    using (var deflate = new DeflateStream(op.Stream, CompressionLevel.Optimal, leaveOpen: true))
                    {
                        var crc32stream = new CRC32Stream(deflate);
                        var state = GetInitialChunkState();
                        foreach (var item in items)
                        {
                            memoryStream.Position = 0;
                            memoryStream.SetLength(0);
                            byte itemFormatVersion = WriteItem(memoryStream, item, state);
                            if (itemFormatAll < 0)
                                itemFormatAll = itemFormatVersion;
                            else if (itemFormatAll != itemFormatVersion)
                                throw new Exception("Inconsistent item format.");
                            if (op.OldestItemFormatVersion == 0)
                                op.OldestItemFormatVersion = itemFormatVersion;
                            crc32stream.WriteUInt64Optim((ulong) memoryStream.Length); // ulong is very slightly more compact than long
                            crc32stream.Write(memoryStream.GetBuffer(), 0, (int) memoryStream.Length);
                            op.CompressedItemsCount++;
                        }
                        crc32 = crc32stream.CRC;
                        op.ShortChunkCount++;
                    }
                    long chunkLength = op.Stream.Position - chunkStartPos;
                    if (chunkLength > uint.MaxValue)
                        throw new Exception("Chunk too long - should have aborted earlier");
                    op.Writer.Write(crc32);
                    op.ValidLength = op.Stream.Position;
                    op.Stream.Position = chunkStartPos - 5;
                    op.Writer.Write((byte) itemFormatAll);
                    op.Writer.Write((uint) chunkLength);
                }
                else
                {
                    var memoryStream = new MemoryStream(); // reuse it to avoid constant array reallocation
                    var memoryWriter = new BinaryWriter(memoryStream);
                    foreach (var item in items)
                    {
                        op.Writer.Write((byte) 2); // chunk type - uncompressed
                        memoryStream.Position = 0;
                        memoryStream.SetLength(0);
                        byte itemFormat = WriteItem(memoryStream, item, GetInitialChunkState()); // we aren't attempting to save multiple uncompressed items into a single chunk for simplicity
                        op.Writer.Write(itemFormat);
                        op.Stream.WriteUInt64Optim((ulong) memoryStream.Length); // ulong is very slightly more compact than long
                        op.Stream.Write(memoryStream.GetBuffer(), 0, (int) memoryStream.Length);
                        op.UncompressedItemsCount++;
                    }
                    op.ValidLength = op.Stream.Position;
                }
                rewriteNeeded = op.ShortChunkCount > 3000 || op.UncompressedItemsCount > 10000;

                return Enumerable.Empty<bool>();
            }).ToList();

            if (rewriteNeeded)
                Rewrite();
        }
    }

    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public class DataTypeIdAttribute : Attribute
    {
        public string Id { get; private set; }
        private static Dictionary<Type, string> _cache = new Dictionary<Type, string>();

        public DataTypeIdAttribute(string id)
        {
            if (id.Length != 4 || id.ToUtf8().Length != 4)
                throw new Exception();
            Id = id;
        }

        public static string GetId(Type type)
        {
            string result;
            if (_cache.TryGetValue(type, out result))
                return result;
            _cache[type] = type.GetCustomAttribute<DataTypeIdAttribute>().Id;
            return _cache[type];
        }
    }
}
