using System.IO;
using System.Text;
using System.Xml;

namespace ZViewer.Services
{
    public interface IXmlFormatterService
    {
        string FormatXml(string xml);
    }

    public class XmlFormatterService : IXmlFormatterService
    {
        public string FormatXml(string xml)
        {
            try
            {
                var doc = new XmlDocument();
                doc.LoadXml(xml);

                var settings = new XmlWriterSettings
                {
                    Indent = true,
                    IndentChars = "  ",
                    NewLineChars = "\r\n",
                    NewLineHandling = NewLineHandling.Replace
                };

                using var stringWriter = new StringWriter();
                using var xmlWriter = XmlWriter.Create(stringWriter, settings);
                doc.Save(xmlWriter);
                return stringWriter.ToString();
            }
            catch
            {
                return xml; // Return original if formatting fails
            }
        }
    }
}