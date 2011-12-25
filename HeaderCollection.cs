using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Mail;
using System.Text.RegularExpressions;

namespace AE.Net.Mail
{
    public class SafeDictionary<KT, VT> : Dictionary<KT, VT>
    {
        public SafeDictionary()
        {
        }

        public SafeDictionary(IEqualityComparer<KT> comparer) : base(comparer)
        {
        }

        public new VT this[KT key]
        {
            get { return this.Get(key); }
            set { this.Set(key, value); }
        }
    }

    public struct HeaderValue
    {
        private readonly string _RawValue;
        private readonly SafeDictionary<string, string> _Values;

        public HeaderValue(string value)
            : this()
        {
            _Values = new SafeDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _RawValue = (value ?? (value = string.Empty));
            _Values[string.Empty] = RawValue;

            int semicolon = value.IndexOf(';');
            if (semicolon > 0)
            {
                _Values[string.Empty] = value.Substring(0, semicolon).Trim();
                value = value.Substring(semicolon).Trim();
                ParseValues(_Values, value);
            }
        }

        public string Value
        {
            get { return this[string.Empty] ?? string.Empty; }
        }

        public string RawValue
        {
            get
            {
                if (string.IsNullOrWhiteSpace(_RawValue))
                    return string.Empty;

                return _RawValue;
            }
        }

        public string RawValueWithoutMarkers
        {
            get
            {
                if (string.IsNullOrWhiteSpace(_RawValue))
                    return string.Empty;

                string pattern = @"^<(?<content>.*)>$";
                MatchCollection matches = Regex.Matches(_RawValue, pattern, RegexOptions.CultureInvariant);
                if (matches.Count == 0 || matches[0].Groups.Count != 2)
                    return _RawValue;

                return matches[0].Groups["content"].ToString();
            }
        }

        public string this[string name]
        {
            get { return _Values.Get(name, string.Empty); }
        }

        public static void ParseValues(IDictionary<string, string> result, string header)
        {
            while (header.Length > 0)
            {
                int eq = header.IndexOf('=');
                if (eq < 0) eq = header.Length;
                string name = header.Substring(0, eq).Trim().Trim(new[] {';', ','}).Trim();

                string value = header = header.Substring(Math.Min(header.Length, eq + 1)).Trim();

                if (value.StartsWith("\""))
                {
                    ProcessValue(1, ref header, ref value, '"');
                }
                else if (value.StartsWith("'"))
                {
                    ProcessValue(1, ref header, ref value, '\'');
                }
                else
                {
                    ProcessValue(0, ref header, ref value, ' ', ',', ';');
                }

                result.Set(name, value);
            }
        }

        private static void ProcessValue(int skip, ref string header, ref string value, params char[] lookFor)
        {
            int quote = value.IndexOfAny(lookFor, skip);
            if (quote < 0) quote = value.Length;
            header = header.Substring(Math.Min(quote + 1, header.Length));
            value = value.Substring(skip, quote - skip);
        }

        public override string ToString()
        {
            IEnumerable<string> props =
                _Values.Where(x => !string.IsNullOrEmpty(x.Key)).Select(x => x.Key + "=" + x.Value);
            return Value + (props.Any() ? ("; " + string.Join(", ", props)) : null);
        }
    }

    public class HeaderCollection : SafeDictionary<string, HeaderValue>
    {
        private static readonly Regex[] rxDates = new[]
                                                      {
                                                          @"\d{1,2}\s+[a-z]{3}\s+\d{2,4}\s+\d{1,2}\:\d{2}\:\d{1,2}\s+[\+\-\d\:]*"
                                                          ,
                                                          @"\d{4}\-\d{1,2}-\d{1,2}\s+\d{1,2}\:\d{2}(?:\:\d{2})?(?:\s+[\+\-\d:]+)?"
                                                          ,
                                                      }.Select(
                                                          x =>
                                                          new Regex(x, RegexOptions.Compiled | RegexOptions.IgnoreCase))
            .ToArray();

        public HeaderCollection() : base(StringComparer.OrdinalIgnoreCase)
        {
        }

        public string GetBoundary()
        {
            return this["Content-Type"]["boundary"];
        }

        public DateTime GetDate()
        {
            DateTime? value = this["Date"].RawValue.ToNullDate();
            if (value == null)
            {
                foreach (Regex rx in rxDates)
                {
                    Match match = rx.Matches(this["Received"].RawValue ?? string.Empty)
                        .Cast<Match>().LastOrDefault();
                    if (match != null)
                    {
                        value = match.Value.ToNullDate();
                        if (value != null)
                        {
                            break;
                        }
                    }
                }
            }

            //written this way so a break can be set on the null condition
            if (value == null) return DateTime.MinValue;
            return value.Value;
        }

        public T GetEnum<T>(string name) where T : struct, IConvertible
        {
            string value = this[name].RawValue;
            if (string.IsNullOrEmpty(value)) return default(T);
            T[] values = Enum.GetValues(typeof (T)).Cast<T>().ToArray();
            return values.FirstOrDefault(x => x.ToString().Equals(value, StringComparison.OrdinalIgnoreCase));
        }

        public MailAddress[] GetAddresses(string header)
        {
            string values = this[header].RawValue.Trim();
            var addrs = new List<MailAddress>();
            while (true)
            {
                int semicolon = values.IndexOf(';');
                int comma = values.IndexOf(',');
                if (comma < semicolon || semicolon == -1) semicolon = comma;

                int bracket = values.IndexOf('>');
                string temp = null;
                if (semicolon == -1 && bracket == -1)
                {
                    if (values.Length > 0) addrs.Add(values.ToEmailAddress());
                    return addrs.Where(x => x != null).ToArray();
                }
                if (bracket > -1 && (semicolon == -1 || bracket < semicolon))
                {
                    temp = values.Substring(0, bracket + 1);
                    values = values.Substring(temp.Length);
                }
                else if (semicolon > -1 && (bracket == -1 || semicolon < bracket))
                {
                    temp = values.Substring(0, semicolon);
                    values = values.Substring(semicolon + 1);
                }
                if (temp.Length > 0)
                    addrs.Add(temp.Trim().ToEmailAddress());
                values = values.Trim();
            }
        }


        public static HeaderCollection Parse(string headers)
        {
            headers = Utilities.DecodeWords(headers);
            var temp = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string[] lines = headers.Split(new[] {'\r', '\n'}, StringSplitOptions.RemoveEmptyEntries);
            int i;
            string key = null, value;
            foreach (string line in lines)
            {
                if (key != null && (line[0] == '\t' || line[0] == ' '))
                {
                    temp[key] += line.Trim();
                }
                else
                {
                    i = line.IndexOf(':');
                    if (i > -1)
                    {
                        key = line.Substring(0, i).Trim();
                        value = line.Substring(i + 1).Trim();
                        temp.Set(key, value);
                    }
                }
            }

            var result = new HeaderCollection();
            foreach (var item in temp)
            {
                result.Add(item.Key, new HeaderValue(item.Value));
            }
            return result;
        }
    }
}