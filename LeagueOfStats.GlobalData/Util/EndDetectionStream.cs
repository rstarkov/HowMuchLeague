using System;
using System.IO;

namespace LeagueOfStats.GlobalData
{
    class EndDetectionStream : Stream
    {
        private Stream _stream;
        private int _nextByte = -1; // -1 means unknown; -2 means stream has ended
        private byte[] _buf = new byte[1];

        public EndDetectionStream(Stream stream)
        {
            _stream = stream;
            _nextByte = -1;
        }

        public override bool CanSeek { get { return false; } }
        public override bool CanRead { get { return _stream.CanRead; } }
        public override bool CanWrite { get { return false; } }

        public override void Flush() { throw new NotSupportedException(); }
        public override void SetLength(long value) { throw new NotSupportedException(); }
        public override long Length { get { throw new NotSupportedException(); } }
        public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
        public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (count == 0 || _nextByte == -2)
                return 0;
            if (_nextByte >= 0)
            {
                buffer[offset] = (byte) _nextByte;
                _nextByte = -1;
                return 1;
            }
            int read = _stream.Read(buffer, offset, count);
            if (read == 0)
                _nextByte = -2;
            return read;
        }

        public bool IsEnded
        {
            get
            {
                if (_nextByte >= 0)
                    return false;
                if (_nextByte == -2)
                    return true;
                int read = _stream.Read(_buf, 0, 1);
                _nextByte = read == 0 ? -2 : _buf[0];
                return _nextByte < 0;
            }
        }
    }
}
