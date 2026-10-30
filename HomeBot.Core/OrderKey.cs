using PKHeXKR;
namespace HomeBot.Core;

/// <summary>주문 해석 결과를 "같은 주문"끼리 같은 문자열이 되게 정규화한 키. 풀 재사용의 기준.</summary>
public static class OrderKey
{
    public static string Of(OrderSpec o)
    {
        var p = new List<string> { $"s{o.Species}", $"f{o.Form}" };
        if (o.Gender >= 0) p.Add($"g{o.Gender}");
        if (o.Shiny != 0) p.Add($"sh{o.Shiny}");
        if (o.Alpha >= 0) p.Add($"a{o.Alpha}");
        if (o.Ball > 0) p.Add($"b{o.Ball}");
        if (o.Nature >= 0) p.Add($"n{o.Nature}");
        if (o.Ability >= 0) p.Add($"ab{o.Ability}");
        if (o.ScaleMin >= 0 || o.ScaleMax >= 0) p.Add($"sc{o.ScaleMin}-{o.ScaleMax}");
        if (o.FlawlessIVs > 0) p.Add($"v{o.FlawlessIVs}");
        if (o.ExactIV.Any(x => x >= 0)) p.Add("iv" + string.Join(".", o.ExactIV));
        if (o.EV.Any(x => x >= 0)) p.Add("ev" + string.Join(".", o.EV));
        if (o.Level > 0) p.Add($"l{o.Level}");
        if (o.Tera >= 0) p.Add($"t{o.Tera}");
        if (o.Gmax) p.Add("gmax");
        if (o.HyperTrain) p.Add("ht");
        if (o.Event) p.Add("ev:" + string.Join(",", o.Keywords));
        if (o.Mark >= 0) p.Add($"m{o.Mark}");
        if (o.Ribbons.Count > 0) p.Add("r:" + string.Join(",", o.Ribbons.OrderBy(x => x)));
        if (o.Moves.Count > 0) p.Add("mv:" + string.Join(",", o.Moves.OrderBy(x => x)));
        if (o.OT != null || o.TID >= 0) p.Add($"ot:{o.OT}/{o.TID}/{o.SID}");
        if (o.MetFrom != null) p.Add($"d{o.MetFrom:yyyyMMdd}-{o.MetTo:yyyyMMdd}");
        return string.Join("|", p);
    }
}
