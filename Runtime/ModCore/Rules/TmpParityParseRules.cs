using System;

namespace LightSide
{
    /// <summary>Parses <![CDATA[<nobr>text</nobr>]]> (TextMeshPro no-break span).</summary>
    /// <seealso cref="NoBreakModifier"/>
    [Serializable]
    [TypeGroup("Tags", 0)]
    public sealed class NoBreakParseRule : TagParseRule
    {
        protected override string TagName => "nobr";
    }

    /// <summary>Parses <![CDATA[<align=left|center|right|justified|flush>text</align>]]>.</summary>
    /// <seealso cref="AlignModifier"/>
    [Serializable]
    [TypeGroup("Tags", 0)]
    public sealed class AlignParseRule : TagParseRule
    {
        protected override string TagName => "align";
        protected override bool HasParameter => true;
    }

    /// <summary>Parses <![CDATA[<indent=N|N%|Nem>text</indent>]]>.</summary>
    /// <seealso cref="IndentModifier"/>
    [Serializable]
    [TypeGroup("Tags", 0)]
    public sealed class IndentParseRule : TagParseRule
    {
        protected override string TagName => "indent";
        protected override bool HasParameter => true;
    }

    /// <summary>Parses <![CDATA[<line-indent=N|N%|Nem>text</line-indent>]]>.</summary>
    /// <seealso cref="IndentModifier"/>
    [Serializable]
    [TypeGroup("Tags", 0)]
    public sealed class LineIndentParseRule : TagParseRule
    {
        protected override string TagName => "line-indent";
        protected override bool HasParameter => true;
    }

    /// <summary>Parses <![CDATA[<font="Name">text</font>]]>.</summary>
    /// <seealso cref="FontModifier"/>
    [Serializable]
    [TypeGroup("Tags", 0)]
    public sealed class FontParseRule : TagParseRule
    {
        protected override string TagName => "font";
        protected override bool HasParameter => true;
    }

    /// <summary>Parses <![CDATA[<smallcaps>text</smallcaps>]]>.</summary>
    /// <seealso cref="SmallCapsModifier"/>
    [Serializable]
    [TypeGroup("Tags", 0)]
    public sealed class SmallCapsParseRule : TagParseRule
    {
        protected override string TagName => "smallcaps";
    }

    /// <summary>
    /// Parses <![CDATA[<mark>text</mark>]]> and <![CDATA[<mark=#RRGGBBAA>text</mark>]]> (TextMeshPro
    /// highlight). Without a colour the TMP default #FFFF0040 is used.
    /// </summary>
    /// <seealso cref="MarkModifier"/>
    [Serializable]
    [TypeGroup("Tags", 0)]
    public sealed class MarkParseRule : IParseRule
    {
        [Serializable]
        private sealed class WithColor : TagParseRule
        {
            protected override string TagName => "mark";
            protected override bool HasParameter => true;
        }

        [Serializable]
        private sealed class Plain : TagParseRule
        {
            protected override string TagName => "mark";
        }

        [NonSerialized] private WithColor withColor;
        [NonSerialized] private Plain plain;

        public int TryMatch(ReadOnlySpan<char> text, int index, PooledList<ParsedRange> results)
        {
            withColor ??= new WithColor();
            plain ??= new Plain();
            // Opening tags first (either form), then the close of whichever form is open (innermost =
            // the most recent open; each form tracks its own stack).
            var r = withColor.TryMatch(text, index, results);
            if (r > index) return r;
            return plain.TryMatch(text, index, results);
        }

        public void Finalize(ReadOnlySpan<char> text, PooledList<ParsedRange> results)
        {
            withColor?.Finalize(text, results);
            plain?.Finalize(text, results);
        }

        public void Reset()
        {
            withColor?.Reset();
            plain?.Reset();
        }
    }
}
