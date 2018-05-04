using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RT.Util.ExtensionMethods;

namespace LeagueOfStats.GlobalData
{
    [DataTypeId("BMIC")]
    public class BasicMatchInfoContainer : LosContainer<BasicMatchInfo, BasicMatchInfoContainer.ChunkState>
    {
        public Region Region { get; private set; }

        public BasicMatchInfoContainer(string filename, Region region) : base(filename)
        {
            Region = region;
        }

        public BasicMatchInfoContainer(string filename) : base(filename)
        {
            operateOnFile(write: false, operation: op =>
            {
                if (op == null)
                    return Enumerable.Empty<bool>();
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

        public class ChunkState
        {
            public long PrevMatchId;
            public int PrevQueueId;
            public int PrevGameVersion;
            public long PrevGameCreation;
        }

        protected override ChunkState GetInitialChunkState()
        {
            return new ChunkState();
        }

        protected override LosContainer<BasicMatchInfo, ChunkState> Clone(string filename)
        {
            return new BasicMatchInfoContainer(filename, Region);
        }

        protected override BasicMatchInfo ReadItem(Stream stream, byte itemFormatVersion, ChunkState state)
        {
            if (itemFormatVersion == 1)
            {
                var result = new BasicMatchInfo();
                result.MatchId = state.PrevMatchId + stream.ReadInt64Optim();
                state.PrevMatchId = result.MatchId;
                result.QueueId = state.PrevQueueId + stream.ReadInt32Optim();
                state.PrevQueueId = result.QueueId;
                var ver = state.PrevGameVersion + stream.ReadInt32Optim();
                state.PrevGameVersion = ver;
                result.GameVersionMajor = (byte) (ver >> 8);
                result.GameVersionMinor = (byte) (ver & 0xFF);
                result.GameCreation = state.PrevGameCreation + stream.ReadInt64Optim();
                state.PrevGameCreation = result.GameCreation;
                return result;
            }
            else
                throw new NotSupportedException($"Match format {itemFormatVersion} is not supported.");
        }

        protected override byte WriteItem(Stream stream, BasicMatchInfo item, ChunkState state)
        {
            stream.WriteInt64Optim(item.MatchId - state.PrevMatchId);
            state.PrevMatchId = item.MatchId;
            stream.WriteInt32Optim(item.QueueId - state.PrevQueueId);
            state.PrevQueueId = item.QueueId;
            var ver = (item.GameVersionMajor << 8) + item.GameVersionMinor;
            stream.WriteInt32Optim(ver - state.PrevGameVersion);
            state.PrevGameVersion = ver;
            stream.WriteInt64Optim(item.GameCreation - state.PrevGameCreation);
            state.PrevGameCreation = item.GameCreation;
            return 1;
        }

        public override void Rewrite(Func<IEnumerable<BasicMatchInfo>, IEnumerable<BasicMatchInfo>> filter = null)
        {
            if (filter == null)
                filter = c => c;
            var existing = new HashSet<long>();
            base.Rewrite(matches => filter(matches).OrderBy(m => m.MatchId).Where(m => existing.Add(m.MatchId)));
        }
    }

}
