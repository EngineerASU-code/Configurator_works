using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;
using Xunit;

namespace Configurator.Security.Tests;

public sealed class SpreadsheetXmlTests
{
    [Fact]
    public void Export_PreservesCellTypesEscapingAndColumnReferences()
    {
        var file = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".xlsx");
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ru-RU");
            var values = new object[] { null, true, 12.5m, "<&\u0001\n", new DateTime(2026, 11, 13) };
            ExcelExportService.Export(file, "Test", new[] { "Empty", "Bool", "Number", "Text", "Date" },
                new[] { values });
            using var zip = ZipFile.OpenRead(file);
            using var stream = zip.GetEntry("xl/worksheets/sheet1.xml").Open();
            var xml = XDocument.Load(stream);
            XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            var cells = xml.Descendants(ns + "row").Last().Elements(ns + "c").ToArray();
            Assert.Empty(cells[0].Elements());
            Assert.Equal("b", (string)cells[1].Attribute("t"));
            Assert.Equal("1", cells[1].Element(ns + "v").Value);
            Assert.Equal("12.5", cells[2].Element(ns + "v").Value);
            Assert.Equal("<&\n", cells[3].Descendants(ns + "t").Single().Value);
            Assert.Equal(Convert.ToString(values[4]), cells[4].Descendants(ns + "t").Single().Value);
            Assert.Equal("XFD", SpreadsheetXml.ColumnName(16384));
            var databaseDate = XElement.Parse(SpreadsheetXml.Cell(values[4], 2, 27, 1, CultureInfo.InvariantCulture));
            Assert.Equal("AA2", (string)databaseDate.Attribute("r"));
            Assert.Equal("1", (string)databaseDate.Attribute("s"));
            Assert.Equal(Convert.ToString(values[4], CultureInfo.InvariantCulture), databaseDate.Descendants("t").Single().Value);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            File.Delete(file);
        }
    }
}
