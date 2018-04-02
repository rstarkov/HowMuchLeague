using System;
using System.IO;

namespace LeagueOfStats.GlobalData
{
    class WindowStream : Stream
    {
        private Stream _stream;
        private long _startPosition;
        private long _position;
        private long _length;

        public WindowStream(Stream stream, long length)
        {
            if (!stream.CanRead)
                throw new NotSupportedException("Only readable streams are supported.");
            _stream = stream;
            _position = 0;
            _length = length;
            if (stream.CanSeek)
                _startPosition = stream.Position;
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            while (_position < _length)
            {
                var buf = new byte[_length - _position];
                _position += _stream.Read(buf, 0, buf.Length);
            }
        }

        public override bool CanSeek { get { return _stream.CanSeek; } }
        public override bool CanRead { get { return true; } }
        public override bool CanWrite { get { return false; } }
        public override long Length { get { return _length; } }

        public override void Flush() { throw new NotSupportedException(); }
        public override void SetLength(long value) { throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }

        public override long Position
        {
            get
            {
                return _position;
            }
            set
            {
                if (value < 0 || value > _length)
                    throw new InvalidOperationException("Cannot seek outside of the window.");
                _stream.Position = _startPosition + value;
                _position = value;
            }
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            var originPos = origin == SeekOrigin.Begin ? 0 : origin == SeekOrigin.Current ? Position : _length;
            Position = originPos + offset;
            _position = Position;
            return Position;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position == _length)
                return 0;
            if (count > _length - _position)
                count = (int) (_length - _position);
            int read = _stream.Read(buffer, offset, count);
            if (read == 0)
                throw new EndOfStreamException("Unexpected end of the underlying stream.");
            _position += read;
            return read;
        }
    }
}
