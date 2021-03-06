﻿using TanjiCore.Flazzy.IO;

namespace TanjiCore.Flazzy.ABC.AVM2.Instructions
{
    public class DebugLineIns : ASInstruction
    {
        public int LineNumber { get; set; }

        public DebugLineIns()
            : base(OPCode.DebugLine)
        { }
        public DebugLineIns(FlashReader input)
            : this()
        {
            LineNumber = input.ReadInt30();
        }

        protected override void WriteValuesTo(FlashWriter output)
        {
            output.WriteInt30(LineNumber);
        }
    }
}