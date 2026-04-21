using System;
using System.ComponentModel;

namespace visland.Helpers;

[AttributeUsage(AttributeTargets.Field)]
internal sealed class LocalizedDescriptionAttribute(string english, string simplifiedChinese) : DescriptionAttribute
{
    private readonly string _english = english;
    private readonly string _simplifiedChinese = simplifiedChinese;

    public override string Description => Loc.Tr(_english, _simplifiedChinese);
}
