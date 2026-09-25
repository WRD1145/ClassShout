namespace ClassShout.Core.Remote;

/// <summary>
/// 一行 CSV 的切分。
///
/// 老师和管理员手里的 CSV 多半是从 Excel 里复制出来的，而 Excel 在字段里出现
/// 逗号时会自动给这个字段加引号（<c>"张三, 小张"</c>）。直接 <c>Split(',')</c>
/// 会把它切成两列、**后面几列全部错位**，而且是静默的：要到教室里看见
/// 「李四（A组）」或者账号列表里科目串了行才会发现。
///
/// 所以学生名单的导入与控制台的账号导入共用这一份实现 ——
/// 同一件事写两遍，迟早会有一边忘了改。
/// </summary>
public static class CsvLine
{
    /// <summary>
    /// 按逗号切列，认双引号包裹：引号里的逗号不是分隔符，
    /// 引号里的一对连续引号表示一个引号本身（CSV 的老规矩）。
    ///
    /// 没配对的引号按"一直到行尾都是内容"处理，不报错：这份内容是从别处粘来的，
    /// 为一个引号把整行丢掉，比读进来一个多带引号的值更糟。
    /// </summary>
    public static string[] Split(string line)
    {
        var fields = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];

            if (inQuotes)
            {
                if (ch != '"')
                {
                    current.Append(ch);
                    continue;
                }

                if (i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = false;
                }

                continue;
            }

            switch (ch)
            {
                case '"':
                    inQuotes = true;
                    break;

                case ',':
                    fields.Add(current.ToString().Trim());
                    current.Clear();
                    break;

                default:
                    current.Append(ch);
                    break;
            }
        }

        fields.Add(current.ToString().Trim());
        return [.. fields];
    }
}
