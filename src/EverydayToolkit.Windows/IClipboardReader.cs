using System.Windows;
namespace EverydayToolkit.Windows;

internal interface IClipboardReader
{
    uint Sequence { get; }
    bool IsExcluded(IReadOnlyList<string>? exclusions);
    System.Windows.IDataObject? GetDataObject();
    bool FormatPresent(string format);
    byte[]? ReadPng();
    bool BitmapPresent { get; }
}
