﻿namespace TanjiCore.Flazzy.ABC
{
    public abstract class AS3Item : FlashItem
    {
        protected ABCFile ABC { get; }

        public AS3Item(ABCFile abc)
        {
            ABC = abc;
        }

        public ABCFile GetABC()
        {
            return ABC;
        }
        public abstract string ToAS3();
    }
}