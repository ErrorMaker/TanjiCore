﻿using System;

namespace TanjiCore.Flazzy.ABC.AVM2.Instructions
{
    public class GetLocal2Ins : Local
    {
        public override int Register
        {
            get { return 2; }
            set { throw new NotSupportedException(); }
        }

        public GetLocal2Ins()
            : base(OPCode.GetLocal_2)
        { }
    }
}