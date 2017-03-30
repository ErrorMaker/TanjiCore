﻿using System;
namespace TanjiCore.Flazzy.ABC.AVM2.Instructions
{
    public class GetLocal1Ins : Local
    {
        public override int Register
        {
            get { return 1; }
            set { throw new NotSupportedException(); }
        }

        public GetLocal1Ins()
            : base(OPCode.GetLocal_1)
        { }
    }
}