using System.Text;
using ExcelDataReader;

namespace ClassShout.Core.Remote;

/// <summary>
/// 从 Excel（.xlsx / .xls）里读一份名单。
///
/// 为什么要支持它：老师手里的名单九成是个表格 —— 教务系统导出的、或者自己在 Excel 里
/// 敲的。"请你先另存为 CSV 再导入"是多出来的一道无用功，而这道工序恰恰最容易出错
/// （另存为时选错编码、或者 Excel 把学号前面的 0 吃掉）。
///
/// 读出来的行会交给 <see cref="RosterCsv.FromRows"/> 走**同一套**解析规则 ——
/// 表头、空行、缺列的处理与 CSV 完全一致，两种入口不会给出两个结果。
/// </summary>
public static class RosterExcel
{
    /// <summary>这些扩展名按 Excel 读。</summary>
    public static bool LooksLikeExcel(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        return extension is ".xlsx" or ".xlsm" or ".xls" or ".xlsb";
    }

    /// <summary>读一张表（第一个工作表）。</summary>
    /// <param name="stream">文件内容。<b>必须可读且支持 Seek</b>（Excel 需要回头读中央目录）。</param>
    /// <param name="rosterName">名单名。</param>
    /// <param name="source">来源文件名（用于提示）。</param>
    public static RosterImportResult Read(Stream stream, string rosterName, string? source = null)
    {
        if (stream is null || !stream.CanRead)
        {
            return new RosterImportResult(null, ["这个文件读不出来。"]);
        }

        try
        {
            // .xls（老格式）是按代码页存的，.NET 默认不带这些编码表。
            // 不注册的话，打开这种文件会抛"没有可用的编码"而不是给出人话。
            //
            // 这一步单独兜住：它在读任何文件之前都会执行，万一某个平台（安卓上
            // BCL 是按需裁剪的）没有这个类型，让它把 .xlsx 一起带崩就太冤了 ——
            // 缺了它最多是老格式的 .xls 读不出来，而 .xlsx 根本用不到代码页。
            TryRegisterCodePages();

            using var reader = ExcelReaderFactory.CreateReader(stream);

            // 只读第一个工作表：老师的名单就是一个班一张表，多出来的往往是模板或说明页。
            // 名字取得出来时把它当作名单名（比文件名更贴近内容）。
            var sheetName = TryGetSheetName(reader);
            var name = string.IsNullOrWhiteSpace(sheetName) ? rosterName : sheetName;

            var rows = new List<IReadOnlyList<string?>>();

            do
            {
                while (reader.Read())
                {
                    var fields = new string?[reader.FieldCount];
                    for (var i = 0; i < reader.FieldCount; i++)
                    {
                        fields[i] = Cell(reader, i);
                    }

                    rows.Add(fields);
                }
            }
            while (false); // 只用第一个工作表

            var result = RosterCsv.FromRows(rows, name, source);

            return result.Ok
                ? result
                : new RosterImportResult(
                    null,
                    [.. result.SkippedLines, "这张表里没读到学生：第一列应当是姓名（表头可有可无）。"]);
        }
        catch (Exception ex) when (ex is ExcelDataReader.Exceptions.ExcelReaderException
                                      or InvalidDataException
                                      or InvalidOperationException or NotSupportedException or IOException
                                      or FormatException or ArgumentException)
        {
            // 这里必须把 ExcelDataReader 自己的异常也接住：老师很可能把一个 .txt
            // 或者下载到一半的文件改名成 .xlsx 丢进来 —— 那种东西抛的是
            // HeaderException（它只继承 Exception），漏掉的话就是一次崩溃而不是一句提示。
            return new RosterImportResult(null, [$"读这个表格失败：{ex.Message}"]);
        }
    }

    /// <summary>
    /// 把代码页编码表注册上（读老格式 .xls 需要）。
    ///
    /// 失败就算了：这个方法在读任何文件之前都会跑，为它一次失败而放弃整条导入路径
    /// 得不偿失 —— 现代格式（.xlsx/.xlsm）是 UTF-8 的，用不到这些编码表。
    /// </summary>
    private static void TryRegisterCodePages()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }
        catch (Exception ex) when (ex is TypeLoadException or FileNotFoundException
                                      or FileLoadException or NotSupportedException
                                      or PlatformNotSupportedException)
        {
            // 同一条路径上一次就够；注册表是进程级的，重复注册也只是无操作
        }
    }

    /// <summary>把一个单元格转成字符串；认不出来的返回 null。</summary>
    private static string? Cell(IExcelDataReader reader, int index)
    {
        if (reader.IsDBNull(index))
        {
            return null;
        }

        var value = reader.GetValue(index);

        return value switch
        {
            null => null,

            // 学号这类东西在表格里常常是数字：直接用 ToString 会变成 "20250101"（好），
            // 但如果 Excel 把它存成 20250101.0 就会多出小数点 —— 这里顺手去掉。
            double number => number == Math.Floor(number) && Math.Abs(number) < 1e15
                ? ((long)number).ToString()
                : number.ToString(),

            DateTime date => date.ToString("yyyy-MM-dd"),
            _ => value.ToString()?.Trim(),
        };
    }

    private static string? TryGetSheetName(IExcelDataReader reader)
    {
        try
        {
            return reader.Name;
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }
}

/// <summary>
/// 按文件名挑一个读法：表格走 Excel，其余按文本（CSV）读。
///
/// 放在 Core 里，是为了让两个平台头（桌面 / 安卓）只关心"怎么把文件内容拿到手"，
/// 而"这个扩展名该怎么读"只有一处实现。
/// </summary>
public static class RosterFile
{
    /// <summary>支持导入的扩展名（文件选择器用它过滤）。</summary>
    public static IReadOnlyList<string> Extensions { get; } = [".csv", ".txt", ".xlsx", ".xlsm", ".xls"];

    /// <summary>读一个名单文件。</summary>
    /// <param name="fileName">文件名（决定用哪种读法）。</param>
    /// <param name="stream">内容。<b>Excel 需要可 Seek 的流</b>。</param>
    /// <param name="rosterName">名单名；Excel 里能取到工作表名时会用工作表名。</param>
    public static RosterImportResult Read(string fileName, Stream stream, string rosterName)
    {
        var name = string.IsNullOrWhiteSpace(rosterName)
            ? Path.GetFileNameWithoutExtension(fileName)
            : rosterName;

        if (RosterExcel.LooksLikeExcel(fileName))
        {
            return RosterExcel.Read(stream, name, Path.GetFileName(fileName));
        }

        try
        {
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return RosterCsv.Parse(reader.ReadToEnd(), name);
        }
        catch (Exception ex) when (ex is IOException or DecoderFallbackException or UnauthorizedAccessException)
        {
            return new RosterImportResult(null, [$"读这个文件失败：{ex.Message}"]);
        }
    }
}
