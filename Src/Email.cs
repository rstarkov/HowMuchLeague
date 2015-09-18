using System;
using System.Collections.Generic;
using System.Reflection;
using System.Linq;
using System.Net.Mail;
using System.Globalization;
using System.IO;
using System.Text;
using System.Net.Mime;

namespace Stratosphere.Imap
{
    public sealed class ImapMessage
    {
        public long Number { private set; get; }
        public long Uid { private set; get; }
        public DateTime Timestamp { private set; get; }
        public string Subject { private set; get; }
        public MailAddress Sender { private set; get; }
        public MailAddress From { private set; get; }
        public MailAddress ReplyTo { private set; get; }
        public IEnumerable<MailAddress> To { private set; get; }
        public IEnumerable<MailAddress> Cc { private set; get; }
        public IEnumerable<MailAddress> Bcc { private set; get; }
        public string ID { private set; get; }
        public IEnumerable<ImapBodyPart> BodyParts { private set; get; }
        public IDictionary<string, object> ExtensionParameters { private set; get; }

        internal ImapMessage(long number, ImapList list)
            : this(number, list, null)
        { }

        internal ImapMessage(long number, ImapList list, IEnumerable<string> extensionParameterNames)
        {
            Number = number;

            int uidIndex = list.IndexOfString("UID");
            int bodyIndex = list.IndexOfString("BODYSTRUCTURE");
            int envelopeIndex = list.IndexOfString("ENVELOPE");

            if (uidIndex != -1)
            {
                Uid = long.Parse(list.GetStringAt(uidIndex + 1));
            }

            if (envelopeIndex != -1)
            {
                ImapList envelopeList = list.GetListAt(envelopeIndex + 1);

                string timestampString = envelopeList.GetStringAt(0);
                DateTime timestamp;

                if (TryParseTimestamp(timestampString, out timestamp))
                {
                    Timestamp = timestamp;
                }

                Subject = RFC2047Decoder.Parse(envelopeList.GetStringAt(1));
                Sender = ParseAddresses(envelopeList.GetListAt(2)).FirstOrDefault();
                From = ParseAddresses(envelopeList.GetListAt(3)).FirstOrDefault();
                ReplyTo = ParseAddresses(envelopeList.GetListAt(4)).FirstOrDefault();
                To = ParseAddresses(envelopeList.GetListAt(5)).ToArray();
                Cc = ParseAddresses(envelopeList.GetListAt(6)).ToArray();
                Bcc = ParseAddresses(envelopeList.GetListAt(7)).ToArray();
                ID = envelopeList.GetStringAt(8);
            }

            if (bodyIndex != -1)
            {
                ImapList bodyList = list.GetListAt(bodyIndex + 1);

                if (bodyList.Count != 0)
                {
                    BodyParts = ParseBodyParts(string.Empty, bodyList).ToArray();
                }
            }

            if (null != extensionParameterNames)
            {
                var extensionParams = new Dictionary<string, object>();

                foreach (var paramName in extensionParameterNames)
                {
                    int index = list.IndexOfString(paramName);
                    if (index != -1)
                    {
                        int valueIndex = index + 1;
                        object value = null;

                        if (list.IsStringAt(valueIndex))
                        {
                            value = list.GetStringAt(valueIndex);
                        }
                        else if (list.IsListAt(valueIndex))
                        {
                            value = list.GetListAt(valueIndex).ToBasicTypesList();
                        }

                        if (null != value)
                        {
                            extensionParams[paramName] = value;
                        }
                    }
                }

                if (extensionParams.Count > 0)
                {
                    ExtensionParameters = extensionParams;
                }
            }
        }

        private static IEnumerable<ImapBodyPart> ParseBodyParts(string section, ImapList bodyList)
        {
            if (bodyList.IsStringAt(0))
            {
                yield return new ImapBodyPart(string.IsNullOrEmpty(section) ? "1" : section, bodyList);
            }
            else
            {
                string innerSectionPrefix = string.IsNullOrEmpty(section) ? string.Empty : section + ".";

                string mutipartType = bodyList.GetStringAt(bodyList.Count - 4);

                if (!string.IsNullOrEmpty(mutipartType))
                {
                    for (int i = 0; i < bodyList.Count - 4; i++)
                    {
                        string innerSection = innerSectionPrefix + (i + 1).ToString();
                        ImapList innerBodyList = bodyList.GetListAt(i);

                        if (innerBodyList.Count != 0)
                        {
                            foreach (ImapBodyPart part in ParseBodyParts(innerSection, innerBodyList))
                            {
                                yield return part;
                            }
                        }
                    }
                }
            }
        }

        private static IEnumerable<MailAddress> ParseAddresses(ImapList list)
        {
            for (int i = 0; i < list.Count; i++)
            {
                ImapList addressList = list.GetListAt(i);

                string displayName = RFC2047Decoder.Parse(addressList.GetStringAt(0));
                string user = RFC2047Decoder.Parse(addressList.GetStringAt(2));
                string host = RFC2047Decoder.Parse(addressList.GetStringAt(3));

                if (!string.IsNullOrEmpty(user) &&
                    !string.IsNullOrEmpty(host))
                {
                    string addressString = string.Format("{0}@{1}",
                            user, host);

                    MailAddress address = null;

                    try
                    {
                        if (string.IsNullOrEmpty(displayName))
                        {
                            address = new MailAddress(addressString);
                        }
                        else
                        {
                            address = new MailAddress(addressString, displayName);
                        }
                    }
                    catch (FormatException) { }

                    if (address != null)
                    {
                        yield return address;
                    }
                }
            }
        }

        private static bool TryParseTimestamp(string s, out DateTime timestamp)
        {
            if (!string.IsNullOrEmpty(s))
            {
                string[] parts = s.Split(' ');
                string lastPart = parts[parts.Length - 1];

                if (lastPart.StartsWith("("))
                {
                    StringBuilder b = new StringBuilder();

                    for (int i = 0; i < parts.Length - 1; ++i)
                    {
                        if (b.Length != 0)
                        {
                            b.Append(' ');
                        }

                        b.Append(parts[i]);
                    }

                    s = b.ToString();
                }
                else
                {
                    string tz;

                    if (__timezoneAbbreaviations.TryGetValue(lastPart, out tz))
                    {
                        StringBuilder b = new StringBuilder();

                        for (int i = 0; i < parts.Length - 1; ++i)
                        {
                            b.Append(parts[i]);
                            b.Append(' ');
                        }

                        b.Append(tz);
                        s = b.ToString();
                    }
                }

                return DateTime.TryParse(s, null, DateTimeStyles.AdjustToUniversal, out timestamp);
            }

            timestamp = DateTime.MinValue;
            return false;
        }

        static ImapMessage()
        {
            //using (StreamReader reader = new StreamReader(Assembly.GetExecutingAssembly().GetManifestResourceStream("Stratosphere.Imap.TZ.txt")))
            //{
            //    string line;

            //    while ((line = reader.ReadLine()) != null)
            //    {
            //        string[] abbs = line.Split(' ');

            //        for (int i = 1; i < abbs.Length; ++i)
            //        {
            //            __timezoneAbbreaviations.Add(abbs[i], abbs[0]);
            //        }
            //    }
            //}
        }

        private static Dictionary<string, string> __timezoneAbbreaviations = new Dictionary<string, string>();
    }


    public sealed class ImapBodyPart
    {
        internal ImapBodyPart(string section, ImapList list)
        {
            Section = section;

            StringBuilder builder = new StringBuilder(string.Format("{0}/{1}", list.GetStringAt(0).ToLowerInvariant(), list.GetStringAt(1).ToLowerInvariant()));
            ImapList paramsList = list.GetListAt(2);

            for (int i = 0; i < paramsList.Count; i += 2)
            {
                builder.AppendFormat(";{0}=\"{1}\"", paramsList.GetStringAt(i), paramsList.GetStringAt(i + 1));
            }

            try
            {
                ContentType = new ContentType(builder.ToString());
            }
            catch
            {
                ContentType = new ContentType();
            }

            ID = list.GetStringAt(3);
            Description = list.GetStringAt(4);
            Encoding = list.GetStringAt(5);

            int size;

            if (int.TryParse(list.GetStringAt(6), out size))
            {
                Size = size;
            }

            if (string.IsNullOrEmpty(ContentType.CharSet))
            {
                ContentType.CharSet = ASCIIEncoding.ASCII.BodyName;
            }
        }

        public string Section { private set; get; }
        public ContentType ContentType { private set; get; }
        public string ID { private set; get; }
        public string Description { private set; get; }
        public string Encoding { private set; get; }
        public int? Size { private set; get; }
    }


    internal sealed class ImapList
    {
        public List<object> ToBasicTypesList()
        {
            List<object> theList = new List<object>();

            // NOTE:  For now, we'll do this recursively, but better approach is non-recursive.
            foreach (var item in _list)
            {
                if (item is string)
                {
                    theList.Add(item);
                }
                else if (item is ImapList)
                {
                    theList.Add((item as ImapList).ToBasicTypesList());
                }
                else
                {
                    throw new InvalidOperationException(
                        string.Format("Encountered (currently) unsupported list type [{0}].  Unable to transform to basic-types list",
                        item.GetType().Name));
                }
            }

            return theList;
        }

        private readonly List<object> _list = new List<object>();

        private ImapList() { }

        private ImapList(IEnumerator<char> chars)
        {
            StringBuilder aggregateBuilder = new StringBuilder();
            const char EscapeChar = '\\';
            const char QuoteChar = '\"';
            bool isInQuotes = false;
            bool isEscaped = false;

            while (chars.MoveNext())
            {
                if (!isEscaped && isInQuotes && chars.Current == EscapeChar)
                {
                    isEscaped = true;
                }
                else
                {
                    if (chars.Current == QuoteChar && !isEscaped)
                    {
                        isInQuotes = !isInQuotes;
                    }
                    else if (chars.Current == ' ' && !isInQuotes)
                    {
                        if (aggregateBuilder.Length > 0)
                        {
                            AddString(aggregateBuilder.ToString());
                            aggregateBuilder = new StringBuilder();
                        }
                    }
                    else if (chars.Current == '(' && !isInQuotes)
                    {
                        _list.Add(new ImapList(chars));
                    }
                    else if (chars.Current == ')' && !isInQuotes)
                    {
                        break;
                    }
                    else
                    {
                        if (isEscaped && chars.Current != QuoteChar && chars.Current != EscapeChar)
                        {
                            // It wasn't escaping a quote or escape char, so add the escape char back
                            aggregateBuilder.Append(EscapeChar);
                        }

                        aggregateBuilder.Append(chars.Current);
                        isEscaped = false;
                    }
                }
            }

            if (aggregateBuilder.Length > 0)
            {
                AddString(aggregateBuilder.ToString());
            }
        }

        private void AddString(string s)
        {
            if (s == "NIL")
            {
                _list.Add(null);
            }
            else
            {
                _list.Add(s);
            }
        }

        public int Count { get { return _list.Count; } }

        public int IndexOfString(string s)
        {
            return _list.IndexOf(s);
        }

        public bool IsStringAt(int i)
        {
            if (i < Count)
            {
                return _list[i] is string;
            }

            return false;
        }

        public bool IsListAt(int i)
        {
            if (i < Count)
            {
                return _list[i] is ImapList;
            }

            return false;
        }

        public string GetStringAt(int i)
        {
            if (IsStringAt(i))
            {
                return (string) _list[i];
            }

            return string.Empty;
        }

        public ImapList GetListAt(int i)
        {
            if (IsListAt(i))
            {
                return (ImapList) _list[i];
            }

            return Empty;
        }

        public static ImapList Parse(string content)
        {
            using (IEnumerator<char> chars = content.GetEnumerator())
            {
                return new ImapList(chars);
            }
        }

        public static readonly ImapList Empty = new ImapList();
    }


    public static class RFC2047Decoder
    {
        public static string Parse(string input)
        {
            StringBuilder sb = new StringBuilder();
            StringBuilder currentWord = new StringBuilder();
            StringBuilder currentSurroundingText = new StringBuilder();
            bool readingWord = false;
            bool hasSeenAtLeastOneWord = false;

            int wordQuestionMarkCount = 0;
            int i = 0;
            while (i < input.Length)
            {
                char currentChar = input[i];
                char peekAhead;
                switch (currentChar)
                {
                    case '=':
                        peekAhead = (i == input.Length - 1) ? ' ' : input[i + 1];

                        if (!readingWord && peekAhead == '?')
                        {
                            if (!hasSeenAtLeastOneWord
                                || (hasSeenAtLeastOneWord && currentSurroundingText.ToString().Trim().Length > 0))
                            {
                                sb.Append(currentSurroundingText.ToString());
                            }

                            currentSurroundingText = new StringBuilder();
                            hasSeenAtLeastOneWord = true;
                            readingWord = true;
                            wordQuestionMarkCount = 0;
                        }
                        break;

                    case '?':
                        if (readingWord)
                        {
                            wordQuestionMarkCount++;

                            peekAhead = (i == input.Length - 1) ? ' ' : input[i + 1];

                            if (wordQuestionMarkCount > 3 && peekAhead == '=')
                            {
                                readingWord = false;

                                currentWord.Append(currentChar);
                                currentWord.Append(peekAhead);

                                sb.Append(ParseEncodedWord(currentWord.ToString()));
                                currentWord = new StringBuilder();

                                i += 2;
                                continue;
                            }
                        }
                        break;
                }

                if (readingWord)
                {
                    currentWord.Append(('_' == currentChar) ? ' ' : currentChar);
                    i++;
                }
                else
                {
                    currentSurroundingText.Append(currentChar);
                    i++;
                }
            }

            sb.Append(currentSurroundingText.ToString());

            return sb.ToString();
        }

        private static string ParseEncodedWord(string input)
        {
            StringBuilder sb = new StringBuilder();

            if (!input.StartsWith("=?"))
                return input;

            if (!input.EndsWith("?="))
                return input;

            // Get the name of the encoding but skip the leading =?
            string encodingName = input.Substring(2, input.IndexOf("?", 2) - 2);
            Encoding enc = ASCIIEncoding.ASCII;
            if (!string.IsNullOrEmpty(encodingName))
            {
                enc = Encoding.GetEncoding(encodingName);
            }

            // Get the type of the encoding
            char type = input[encodingName.Length + 3];

            // Start after the name of the encoding and the other required parts
            int startPosition = encodingName.Length + 5;

            switch (char.ToLowerInvariant(type))
            {
                case 'q':
                    sb.Append(ParseQuotedPrintable(enc, input, startPosition, true));
                    break;
                case 'b':
                    string baseString = input.Substring(startPosition, input.Length - startPosition - 2);
                    byte[] baseDecoded = Convert.FromBase64String(baseString);
                    var intermediate = enc.GetString(baseDecoded);
                    sb.Append(intermediate);
                    break;
            }
            return sb.ToString();
        }

        public static string ParseQuotedPrintable(Encoding enc, string input)
        {
            return ParseQuotedPrintable(enc, input, 0, false);
        }

        public static string ParseQuotedPrintable(Encoding enc, string input, int startPos, bool skipQuestionEquals)
        {
            byte[] workingBytes = ASCIIEncoding.ASCII.GetBytes(input);

            int i = startPos;
            int outputPos = i;

            while (i < workingBytes.Length)
            {
                byte currentByte = workingBytes[i];
                char[] peekAhead = new char[2];
                switch (currentByte)
                {
                    case (byte) '=':
                        bool canPeekAhead = (i < workingBytes.Length - 2);

                        if (!canPeekAhead)
                        {
                            workingBytes[outputPos] = workingBytes[i];
                            ++outputPos;
                            ++i;
                            break;
                        }

                        int skipNewLineCount = 0;
                        for (int j = 0; j < 2; ++j)
                        {
                            char c = (char) workingBytes[i + j + 1];
                            if ('\r' == c || '\n' == c)
                            {
                                ++skipNewLineCount;
                            }
                        }

                        if (skipNewLineCount > 0)
                        {
                            // If we have a lone equals followed by newline chars, then this is an artificial
                            // line break that should be skipped past.
                            i += 1 + skipNewLineCount;
                        }
                        else
                        {
                            try
                            {
                                peekAhead[0] = (char) workingBytes[i + 1];
                                peekAhead[1] = (char) workingBytes[i + 2];

                                byte decodedByte = Convert.ToByte(new string(peekAhead, 0, 2), 16);
                                workingBytes[outputPos] = decodedByte;

                                ++outputPos;
                                i += 3;
                            }
                            catch (Exception)
                            {
                                // could not parse the peek-ahead chars as a hex number... so gobble the un-encoded '='
                                i += 1;
                            }
                        }
                        break;

                    case (byte) '?':
                        if (skipQuestionEquals && workingBytes[i + 1] == (byte) '=')
                        {
                            i += 2;
                        }
                        else
                        {
                            workingBytes[outputPos] = workingBytes[i];
                            ++outputPos;
                            ++i;
                        }
                        break;

                    default:
                        workingBytes[outputPos] = workingBytes[i];
                        ++outputPos;
                        ++i;
                        break;
                }
            }

            string output = string.Empty;

            int numBytes = outputPos - startPos;
            if (numBytes > 0)
            {
                output = enc.GetString(workingBytes, startPos, numBytes);
            }

            return output;
        }
    }
}