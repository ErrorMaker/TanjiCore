﻿using System.IO;
using System.Text;

namespace TanjiCore.Flazzy.IO
{
    public class FlashReader : BinaryReader
    {
        private readonly bool _leaveOpen;

        public long Length
        {
            get { return BaseStream.Length; }
        }
        public long Position
        {
            get { return BaseStream.Position; }
            set { BaseStream.Position = value; }
        }
        public bool IsDataAvailable
        {
            get { return Position < Length; }
        }

        protected int BitPosition { get; set; }
        protected byte BitContainer { get; set; }

        public FlashReader(byte[] data)
            : this(new MemoryStream(data))
        { }

        public FlashReader(Stream output)
            : this(output, new UTF8Encoding(false, true), false)
        { }
        public FlashReader(Stream output, bool leaveOpen)
            : this(output, new UTF8Encoding(false, true), leaveOpen)
        { }
        public FlashReader(Stream output, Encoding encoding)
            : this(output, encoding, false)
        { }
        public FlashReader(Stream output, Encoding encoding, bool leaveOpen)
            : base(output, encoding)
        {
            _leaveOpen = leaveOpen;
        }

        public uint ReadUInt24()
        {
            var value = (uint)(ReadByte() +
                (ReadByte() << 8) + (ReadByte() << 16));

            if ((value >> 23) == 1)
                value |= 0xff000000;

            return value;
        }

        public int ReadInt30()
        {
            Align();
            return Read7BitEncodedInt();
        }
        public uint ReadUInt30()
        {
            return (uint)ReadInt30();
        }

        public int ReadUB(int bitCount)
        {
            int result = 0;
            if (bitCount > 0)
            {
                if (BitPosition == 0)
                {
                    BitContainer = ReadByte();
                }
                for (int i = 0; i < bitCount; i++)
                {
                    int bit = ((BitContainer >> (7 - BitPosition)) & 1);
                    result += (bit << ((bitCount - 1) - i));

                    if (++BitPosition == 8)
                    {
                        BitPosition = 0;
                        if (i != (bitCount - 1))
                        {
                            BitContainer = ReadByte();
                        }
                    }
                }
            }
            return result;
        }
        public int ReadSB(int bitCount)
        {
            int result = ReadUB(bitCount);
            int shift = (32 - bitCount);

            return ((result << shift) >> shift);
        }

        public string ReadNullString()
        {
            char currentChar = '\0';
            string value = string.Empty;
            while ((currentChar = ReadChar()) != '\0')
            {
                value += currentChar;
            }
            return value;
        }
        public string ReadString(int length)
        {
            char[] characters = ReadChars(length);
            return new string(characters);
        }

        #region Read Overrides
        public override int Read()
        {
            Align();
            return base.Read();
        }
        public override int Read(byte[] buffer, int index, int count)
        {
            Align();
            return base.Read(buffer, index, count);
        }
        public override int Read(char[] buffer, int index, int count)
        {
            Align();
            return base.Read(buffer, index, count);
        }
        public override bool ReadBoolean()
        {
            Align();
            return base.ReadBoolean();
        }
        public override byte ReadByte()
        {
            Align();
            return base.ReadByte();
        }
        public override byte[] ReadBytes(int count)
        {
            Align();
            return base.ReadBytes(count);
        }
        public override char ReadChar()
        {
            Align();
            return base.ReadChar();
        }
        public override char[] ReadChars(int count)
        {
            Align();
            return base.ReadChars(count);
        }
        public override decimal ReadDecimal()
        {
            Align();
            return base.ReadDecimal();
        }
        public override double ReadDouble()
        {
            Align();
            return base.ReadDouble();
        }
        public override short ReadInt16()
        {
            Align();
            return base.ReadInt16();
        }
        public override int ReadInt32()
        {
            Align();
            return base.ReadInt32();
        }
        public override long ReadInt64()
        {
            Align();
            return base.ReadInt64();
        }
        public override sbyte ReadSByte()
        {
            Align();
            return base.ReadSByte();
        }
        public override float ReadSingle()
        {
            Align();
            return base.ReadSingle();
        }
        public override string ReadString()
        {
            Align();
            return base.ReadString();
        }
        public override ushort ReadUInt16()
        {
            Align();
            return base.ReadUInt16();
        }
        public override uint ReadUInt32()
        {
            Align();
            return base.ReadUInt32();
        }
        public override ulong ReadUInt64()
        {
            Align();
            return base.ReadUInt64();
        }
        #endregion

        public void Align()
        {
            if (BitPosition > 0)
            {
                BitPosition = 0;
                BitContainer = 0;
            }
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(!_leaveOpen);
        }
    }
}