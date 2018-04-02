using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RT.Util.ExtensionMethods;

namespace LeagueOfStats.GlobalData
{
    [DataTypeId("MTID")]
    public class MatchIdContainer : LosContainer<long, MatchIdContainer.ChunkState>
    {
        public Region Region { get; private set; }

        public MatchIdContainer(string filename, Region region) : base(filename)
        {
            Region = region;
        }

        public MatchIdContainer(string filename) : base(filename)
        {
            operateOnFile(write: false, operation: op =>
            {
                using (var ms = new MemoryStream(op.FormatSpecificData))
                {
                    var reader = new BinaryReader(ms);
                    var version = reader.ReadByte();
                    if (version == 1)
                    {
                        Region = reader.ReadByte().ToRegion();
                        if (ms.Position != op.FormatSpecificData.Length)
                            throw new Exception("Expected end of format-specific data.");
                    }
                    else
                        throw new Exception();
                }
                return Enumerable.Empty<bool>();
            }).ToList();
        }

        protected override byte[] SerializeFormatSpecificData()
        {
            return new byte[] { 1, Region.ToByte() };
        }

        public class ChunkState { public long PrevId; }

        protected override ChunkState GetInitialChunkState()
        {
            return new ChunkState();
        }

        protected override LosContainer<long, ChunkState> Clone(string filename)
        {
            return new MatchIdContainer(filename, Region);
        }

        protected override long ReadItem(Stream stream, byte itemFormatVersion, ChunkState state)
        {
            if (itemFormatVersion == 1)
            {
                var result = state.PrevId + stream.ReadInt64Optim();
                state.PrevId = result;
                return result;
            }
            else
                throw new NotSupportedException($"Match format {itemFormatVersion} is not supported.");
        }

        protected override byte WriteItem(Stream stream, long item, ChunkState state)
        {
            stream.WriteInt64Optim(item - state.PrevId);
            state.PrevId = item;
            return 1;
        }

        public override void Rewrite(Func<IEnumerable<long>, IEnumerable<long>> filter = null)
        {
            if (filter == null)
                base.Rewrite(matchIds => matchIds.Order());
            else
                base.Rewrite(matchIds => filter(matchIds).Order());
        }
    }

}
