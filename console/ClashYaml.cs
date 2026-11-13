// ClashYaml: 把合并后的节点 URI 行转成 Clash Meta (mihomo) 可用的 clash.yaml。
// 输出：proxies + proxy-groups（PROXY 手动选 / Auto 自动测速）+ rules（MATCH,PROXY）。
// 不支持的协议或传输方式会被跳过并计数，不会让整个文件失效；
// ssr 会降级成 ss（去掉 SSR 专属的 protocol/obfs，与 subconverter 对 Clash 的做法一致）。
//
// 注意：YAML 由本文件的手写发射器生成，不用 YamlDotNet——
// YamlDotNet 18.1.0 会把 "null"、"true"、"123" 这类字符串原样输出（不加引号），
// YAML 解析器会把它们读成 null / 布尔 / 数字，导致 mihomo 加载整个配置失败。
// 手写发射器把所有字符串都用双引号包起来，数字和布尔保持原样。
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ProxyNodeHub;

public static class ClashYaml
{
    /// <summary>转换节点 URI 行；返回 (yaml 文本, 成功数, 跳过数)。成功数为 0 时 yaml 为空串。</summary>
    public static (string yaml, int converted, int skipped) ConvertLines(IEnumerable<string> lines)
    {
        var proxies = new List<Dictionary<string, object>>();
        var usedNames = new HashSet<string>(StringComparer.Ordinal);
        var skipped = 0;
        var index = 0;
        foreach (var raw in lines)
        {
            index++;
            var line = raw.Trim();
            if (line.Length == 0) continue;
            Dictionary<string, object>? proxy;
            string name;
            try { (proxy, name) = ParseNode(line); }
            catch { proxy = null; name = ""; }
            if (proxy is null) { skipped++; continue; }
            if (name.Length == 0) name = $"node-{index}";
            proxy["name"] = UniqueName(name, usedNames);
            proxies.Add(proxy);
        }
        if (proxies.Count == 0) return ("", 0, skipped);

        var names = proxies.Select(p => p["name"]!.ToString()!).ToList();
        var doc = new Dictionary<string, object>
        {
            ["mixed-port"] = 7890,
            ["allow-lan"] = false,
            ["mode"] = "rule",
            ["log-level"] = "info",
            ["external-controller"] = "127.0.0.1:9090",
            ["proxies"] = proxies,
            ["proxy-groups"] = new List<object>
            {
                new Dictionary<string, object>
                {
                    ["name"] = "PROXY",
                    ["type"] = "select",
                    ["proxies"] = names,
                },
                new Dictionary<string, object>
                {
                    ["name"] = "Auto",
                    ["type"] = "url-test",
                    ["url"] = "http://www.gstatic.com/generate_204",
                    ["interval"] = 300,
                    ["proxies"] = names,
                },
            },
            ["rules"] = new List<string> { "MATCH,PROXY" },
        };
        var yaml = EmitYaml(doc);
        return (yaml, proxies.Count, skipped);
    }

    // ---------- 手写 YAML 发射器（所有字符串双引号，数字/布尔原样） ----------

    static string EmitYaml(Dictionary<string, object> doc)
    {
        var sb = new StringBuilder();
        EmitMapping(sb, doc, 0);
        return sb.ToString();
    }

    static void EmitMapping(StringBuilder sb, Dictionary<string, object> map, int indent)
    {
        var pad = new string(' ', indent);
        foreach (var (k, v) in map)
        {
            sb.Append(pad).Append(k).Append(':');
            EmitInlineOrBlock(sb, v, indent);
        }
    }

    static void EmitInlineOrBlock(StringBuilder sb, object? v, int indent)
    {
        switch (v)
        {
            case null:
                sb.Append(" null\n");
                break;
            case string s:
                sb.Append(' ');
                EmitQuoted(sb, s);
                sb.Append('\n');
                break;
            case Dictionary<string, object> map:
                if (map.Count == 0) sb.Append(" {}\n");
                else { sb.Append('\n'); EmitMapping(sb, map, indent + 2); }
                break;
            case System.Collections.IEnumerable seq:
                var items = seq.Cast<object>().ToList();
                if (items.Count == 0) sb.Append(" []\n");
                else { sb.Append('\n'); EmitSequence(sb, items, indent + 2); }
                break;
            default:
                sb.Append(' ');
                EmitScalar(sb, v);
                sb.Append('\n');
                break;
        }
    }

    static void EmitSequence(StringBuilder sb, List<object> list, int indent)
    {
        var pad = new string(' ', indent);
        foreach (var item in list)
        {
            if (item is Dictionary<string, object> map && map.Count > 0)
            {
                var first = true;
                foreach (var (k, v) in map)
                {
                    if (first)
                    {
                        sb.Append(pad).Append("- ").Append(k).Append(':');
                        EmitInlineOrBlock(sb, v, indent + 2);
                        first = false;
                    }
                    else
                    {
                        sb.Append(pad).Append("  ").Append(k).Append(':');
                        EmitInlineOrBlock(sb, v, indent + 2);
                    }
                }
            }
            else
            {
                sb.Append(pad).Append("- ");
                EmitScalar(sb, item);
                sb.Append('\n');
            }
        }
    }

    static void EmitScalar(StringBuilder sb, object? v)
    {
        switch (v)
        {
            case null: sb.Append("null"); break;
            case string s: EmitQuoted(sb, s); break;
            case bool b: sb.Append(b ? "true" : "false"); break;
            case int i: sb.Append(i.ToString(CultureInfo.InvariantCulture)); break;
            case long l: sb.Append(l.ToString(CultureInfo.InvariantCulture)); break;
            default: EmitQuoted(sb, v.ToString() ?? ""); break;
        }
    }

    static void EmitQuoted(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (char.IsControl(c)) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }

    // ---------- 字段校验（mihomo 加载配置时任一非法字段都会让整个文件失效） ----------

    static readonly Regex UuidDashedRe = new("^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$", RegexOptions.Compiled);

    /// <summary>UUID：标准带横杠格式直接用；32 位无横杠 hex 补横杠；其他格式无法修复，返回 null（跳过该节点）。</summary>
    static string? NormalizeUuid(string uuid)
    {
        var u = uuid.Trim();
        if (UuidDashedRe.IsMatch(u)) return u;
        if (u.Length == 32 && u.All(Uri.IsHexDigit))
            return $"{u[..8]}-{u[8..12]}-{u[12..16]}-{u[16..20]}-{u[20..]}";
        return null;
    }

    /// <summary>Reality short-id 必须是偶数长度的 hex，不超过 16 字符；不合法就不要这个字段（空 short-id 是合法的）。</summary>
    static bool ValidShortId(string sid) =>
        sid.Length is >= 2 and <= 16 && sid.Length % 2 == 0 && sid.All(Uri.IsHexDigit);

    static readonly HashSet<string> VmessCiphers = new(StringComparer.OrdinalIgnoreCase)
        { "auto", "aes-128-gcm", "chacha20-poly1305", "none" };

    static readonly HashSet<string> SsCiphers = new(StringComparer.OrdinalIgnoreCase)
    {
        "aes-128-gcm", "aes-256-gcm", "chacha20-ietf-poly1305", "xchacha20-ietf-poly1305",
        "aes-128-ctr", "aes-192-ctr", "aes-256-ctr",
        "aes-128-cfb", "aes-192-cfb", "aes-256-cfb",
        "chacha20-ietf", "xchacha20", "rc4-md5",
        "2022-blake3-aes-128-gcm", "2022-blake3-aes-256-gcm", "2022-blake3-chacha20-poly1305",
        "none",
    };

    static readonly HashSet<string> Fingerprints = new(StringComparer.OrdinalIgnoreCase)
        { "chrome", "firefox", "safari", "ios", "android", "edge", "qq", "random", "randomized" };

    static readonly HashSet<string> VlessFlows = new(StringComparer.OrdinalIgnoreCase)
        { "xtls-rprx-vision", "xtls-rprx-vision-udp443" };

    static string UniqueName(string name, HashSet<string> used)
    {
        if (used.Add(name)) return name;
        var i = 2;
        while (!used.Add($"{name}-{i}")) i++;
        return $"{name}-{i}";
    }

    static (Dictionary<string, object>? proxy, string name) ParseNode(string line)
    {
        var idx = line.IndexOf("://", StringComparison.Ordinal);
        if (idx < 0) return (null, "");
        var scheme = line[..idx].ToLowerInvariant();
        var rest = line[(idx + 3)..];
        return scheme switch
        {
            "vmess" => ParseVmess(rest),
            "vless" => ParseVless(rest),
            "trojan" => ParseTrojan(rest),
            "ss" => ParseSs(rest),
            "ssr" => ParseSsr(rest),
            "hysteria2" or "hy2" => ParseHysteria2(rest),
            "hysteria" or "hy" => ParseHysteria(rest),
            "tuic" => ParseTuic(rest),
            "socks" or "socks5" => ParseSocks(rest),
            "http" or "https" => ParseHttp(rest, scheme == "https"),
            _ => (null, ""),
        };
    }

    // ---------- 通用小工具 ----------

    static (string main, string name) SplitFragment(string rest)
    {
        var hash = rest.IndexOf('#');
        if (hash < 0) return (rest, "");
        return (rest[..hash], Uri.UnescapeDataString(rest[(hash + 1)..]));
    }

    static (string main, string query) SplitQuery(string main)
    {
        var q = main.IndexOf('?');
        if (q < 0) return (main, "");
        return (main[..q], main[(q + 1)..]);
    }

    static Dictionary<string, string> ParseQuery(string query)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (query.Length == 0) return d;
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq < 0) d[WebUtility.UrlDecode(pair)] = "";
            else d[WebUtility.UrlDecode(pair[..eq])] = WebUtility.UrlDecode(pair[(eq + 1)..]);
        }
        return d;
    }

    /// <summary>Base64 / Base64URL 解码（自动补 padding）；失败返回 null。</summary>
    static string? B64Decode(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var t = new string(s.Trim().Where(c => !char.IsWhiteSpace(c)).ToArray())
            .Replace('-', '+').Replace('_', '/');
        if (t.Length % 4 == 1) return null;
        try
        {
            var pad = t.Length % 4;
            if (pad > 0) t += new string('=', 4 - pad);
            return Encoding.UTF8.GetString(Convert.FromBase64String(t));
        }
        catch { return null; }
    }

    static (string? host, int? port) ParseHostPort(string s)
    {
        if (s.StartsWith("["))
        {
            var close = s.IndexOf(']');
            if (close < 0) return (null, null);
            var host = s[1..close];
            var rest = s[(close + 1)..];
            if (rest.StartsWith(":") && int.TryParse(rest[1..], out var p)) return (host, p);
            return (host, null);
        }
        var colon = s.LastIndexOf(':');
        if (colon < 0) return (s.Length > 0 ? s : null, null);
        if (!int.TryParse(s[(colon + 1)..], out var port)) return (null, null);
        var h = s[..colon];
        return (h.Length > 0 ? h : null, port);
    }

    static List<string> SplitList(string s) =>
        s.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();

    /// <summary>Base64 / Base64URL 解码成字节；失败返回 null。</summary>
    static byte[]? B64DecodeBytes(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var t = s.Trim().Replace('-', '+').Replace('_', '/');
        if (t.Length % 4 == 1) return null;
        try
        {
            var pad = t.Length % 4;
            if (pad > 0) t += new string('=', 4 - pad);
            return Convert.FromBase64String(t);
        }
        catch { return null; }
    }

    /// <summary>2022-blake3 系列密码必须是 Base64 编码的定长密钥（128 位 16 字节，256 位 32 字节），
    /// mihomo 加载配置时就会校验，不合法会让整个文件失效；其他加密方式密码任意字符串即可。</summary>
    static bool ValidCipherPassword(string cipher, string password)
    {
        var keyLen = cipher switch
        {
            "2022-blake3-aes-128-gcm" => 16,
            "2022-blake3-aes-256-gcm" or "2022-blake3-chacha20-poly1305" => 32,
            _ => 0,
        };
        if (keyLen == 0) return true;
        var bytes = B64DecodeBytes(password);
        return bytes is not null && bytes.Length == keyLen;
    }

    /// <summary>Clash 要求端口 1-65535。</summary>
    static bool ValidPort(int? port) => port is >= 1 and <= 65535;

    /// <summary>TLS 相关字段：sni / alpn / fp，reality 时再加 public-key / short-id。</summary>
    static void ApplyTls(Dictionary<string, object> d, Dictionary<string, string> q, bool isTls, string security)
    {
        if (!isTls) return;
        d["tls"] = true;
        if (q.TryGetValue("sni", out var sni) && sni.Length > 0) d["servername"] = sni;
        if (q.TryGetValue("alpn", out var alpn) && alpn.Length > 0) d["alpn"] = SplitList(alpn);
        if (q.TryGetValue("fp", out var fp) && fp.Length > 0 && Fingerprints.Contains(fp)) d["fingerprint"] = fp;
        if (security == "reality")
        {
            var ro = new Dictionary<string, object>();
            if (q.TryGetValue("pbk", out var pbk) && pbk.Length > 0) ro["public-key"] = pbk;
            if (q.TryGetValue("sid", out var sid) && ValidShortId(sid)) ro["short-id"] = sid;
            if (ro.Count > 0) d["reality-opts"] = ro;
        }
    }

    /// <summary>传输层：ws / http / h2 / grpc。kcp、quic、domainsocket 只有 V2Ray 支持，返回 false 表示应跳过该节点。</summary>
    static bool ApplyTransport(Dictionary<string, object> d, string net, Dictionary<string, string> q)
    {
        switch (net)
        {
            case "ws":
                var ws = new Dictionary<string, object>();
                if (q.TryGetValue("path", out var path) && path.Length > 0) ws["path"] = path;
                if (q.TryGetValue("host", out var host) && host.Length > 0)
                    ws["headers"] = new Dictionary<string, object> { ["Host"] = host };
                d["ws-opts"] = ws;
                return true;
            case "http":
                // 注意：mihomo 里 http-opts.path 和 headers 的值都必须是列表（slice），不是字符串
                var http = new Dictionary<string, object>();
                if (q.TryGetValue("host", out var hhost) && hhost.Length > 0)
                    http["headers"] = new Dictionary<string, object> { ["Host"] = SplitList(hhost) };
                if (q.TryGetValue("path", out var hpath) && hpath.Length > 0) http["path"] = new List<string> { hpath };
                d["http-opts"] = http;
                return true;
            case "h2":
                var h2 = new Dictionary<string, object>();
                if (q.TryGetValue("host", out var h2host) && h2host.Length > 0) h2["host"] = SplitList(h2host);
                if (q.TryGetValue("path", out var h2path) && h2path.Length > 0) h2["path"] = h2path;
                d["h2-opts"] = h2;
                return true;
            case "grpc":
                var grpc = new Dictionary<string, object>();
                if (q.TryGetValue("serviceName", out var svc) && svc.Length > 0) grpc["grpc-service-name"] = svc;
                d["grpc-opts"] = grpc;
                return true;
            case "xhttp":
                var xhttp = new Dictionary<string, object>();
                if (q.TryGetValue("path", out var xpath) && xpath.Length > 0) xhttp["path"] = xpath;
                if (q.TryGetValue("host", out var xhost) && xhost.Length > 0) xhttp["host"] = xhost;
                if (q.TryGetValue("mode", out var xmode) && xmode is "auto" or "packet-up" or "stream-up" or "stream-one") xhttp["mode"] = xmode;
                d["xhttp-opts"] = xhttp;
                return true;
            case "tcp":
                return true;
            default:
                return false;
        }
    }

    // ---------- 各协议 ----------

    static (Dictionary<string, object>? proxy, string name) ParseVmess(string rest)
    {
        var (main, name) = SplitFragment(rest);
        var json = B64Decode(main);
        if (json is null) return (null, name);
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch { return (null, name); }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return (null, name);
            string S(string k) => root.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            int P(string k)
            {
                if (!root.TryGetProperty(k, out var v)) return 0;
                if (v.ValueKind == JsonValueKind.Number) return v.GetInt32();
                if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var n)) return n;
                return 0;
            }
            var server = S("add");
            var port = P("port");
            var uuid = NormalizeUuid(S("id"));
            if (server.Length == 0 || !ValidPort(port) || uuid is null) return (null, name);
            var net = S("net");
            if (net.Length == 0) net = "tcp";
            if (net is "raw" or "none") net = "tcp"; // raw / none 就是裸 TCP
            if (net is not ("tcp" or "ws" or "http" or "h2" or "grpc" or "xhttp")) return (null, name);
            var scy = S("scy");
            var d = new Dictionary<string, object>
            {
                ["type"] = "vmess",
                ["server"] = server,
                ["port"] = port,
                ["uuid"] = uuid,
                ["alterId"] = P("aid"),
                // scy 不是合法值（比如字面 "null"）时回退到 auto
                ["cipher"] = VmessCiphers.Contains(scy) ? scy : "auto",
            };
            if (net != "tcp") d["network"] = net;
            if (S("tls") == "tls")
            {
                d["tls"] = true;
                var sni = S("sni");
                if (sni.Length > 0) d["servername"] = sni;
                var alpn = S("alpn");
                if (alpn.Length > 0) d["alpn"] = SplitList(alpn);
                var fp = S("fp");
                if (fp.Length > 0 && Fingerprints.Contains(fp)) d["fingerprint"] = fp;
            }
            var q = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["path"] = S("path"),
                ["host"] = S("host"),
            };
            if (net == "tcp" && S("type") == "http")
            {
                // mihomo 里 http-opts.path 和 headers 的值都必须是列表（slice），不是字符串
                var http = new Dictionary<string, object>();
                if (q["host"].Length > 0) http["headers"] = new Dictionary<string, object> { ["Host"] = SplitList(q["host"]) };
                http["path"] = new List<string> { q["path"].Length > 0 ? q["path"] : "/" };
                http["method"] = "GET";
                d["http-opts"] = http;
            }
            else if (!ApplyTransport(d, net, q)) return (null, name);
            return (d, name);
        }
    }

    static (Dictionary<string, object>? proxy, string name) ParseVless(string rest)
    {
        var (main, name) = SplitFragment(rest);
        var (beforeQ, query) = SplitQuery(main);
        var q = ParseQuery(query);
        var at = beforeQ.LastIndexOf('@');
        if (at < 0) return (null, name);
        var uuid = NormalizeUuid(WebUtility.UrlDecode(beforeQ[..at]));
        var (host, port) = ParseHostPort(beforeQ[(at + 1)..]);
        if (host is null || !ValidPort(port) || uuid is null) return (null, name);
        var d = new Dictionary<string, object>
        {
            ["type"] = "vless",
            ["server"] = host,
            ["port"] = port!.Value,
            ["uuid"] = uuid,
        };
        if (q.TryGetValue("flow", out var flow) && VlessFlows.Contains(flow)) d["flow"] = flow;
        var security = q.GetValueOrDefault("security", "");
        ApplyTls(d, q, security is "tls" or "reality", security);
        var net = q.GetValueOrDefault("type", "tcp");
        if (net is "raw" or "none") net = "tcp"; // raw / none 就是裸 TCP
        if (net is not ("tcp" or "ws" or "http" or "h2" or "grpc" or "xhttp")) return (null, name);
        if (net != "tcp") d["network"] = net;
        if (!ApplyTransport(d, net, q)) return (null, name);
        return (d, name);
    }

    static (Dictionary<string, object>? proxy, string name) ParseTrojan(string rest)
    {
        var (main, name) = SplitFragment(rest);
        var (beforeQ, query) = SplitQuery(main);
        var q = ParseQuery(query);
        var at = beforeQ.LastIndexOf('@');
        if (at < 0) return (null, name);
        var password = WebUtility.UrlDecode(beforeQ[..at]);
        var (host, port) = ParseHostPort(beforeQ[(at + 1)..]);
        if (host is null || !ValidPort(port) || password.Length == 0) return (null, name);
        var d = new Dictionary<string, object>
        {
            ["type"] = "trojan",
            ["server"] = host,
            ["port"] = port!.Value,
            ["password"] = password,
        };
        if (q.TryGetValue("sni", out var sni) && sni.Length > 0) d["sni"] = sni;
        if (q.TryGetValue("allowInsecure", out var ai) && ai is "1" or "true") d["skip-cert-verify"] = true;
        if (q.TryGetValue("alpn", out var alpn) && alpn.Length > 0) d["alpn"] = SplitList(alpn);
        var net = q.GetValueOrDefault("type", "tcp");
        if (net is "raw" or "none") net = "tcp"; // raw / none 就是裸 TCP
        if (net is not ("tcp" or "ws" or "http" or "h2" or "grpc" or "xhttp")) return (null, name);
        if (net != "tcp") d["network"] = net;
        if (!ApplyTransport(d, net, q)) return (null, name);
        return (d, name);
    }

    static (Dictionary<string, object>? proxy, string name) ParseSs(string rest)
    {
        var (main, name) = SplitFragment(rest);
        var (beforeQ, query) = SplitQuery(main);
        var q = ParseQuery(query);
        string? method, password, host;
        int? port;
        if (beforeQ.Contains('@'))
        {
            var at = beforeQ.LastIndexOf('@');
            var userinfo = beforeQ[..at];
            var decoded = B64Decode(userinfo);
            if (decoded is not null && decoded.Contains(':')) userinfo = decoded;
            var colon = userinfo.IndexOf(':');
            if (colon < 0) return (null, name);
            method = userinfo[..colon];
            password = userinfo[(colon + 1)..];
            (host, port) = ParseHostPort(beforeQ[(at + 1)..]);
        }
        else
        {
            var decoded = B64Decode(beforeQ);
            if (decoded is null) return (null, name);
            var at = decoded.LastIndexOf('@');
            if (at < 0) return (null, name);
            var mp = decoded[..at];
            var colon = mp.IndexOf(':');
            if (colon < 0) return (null, name);
            method = mp[..colon];
            password = mp[(colon + 1)..];
            (host, port) = ParseHostPort(decoded[(at + 1)..]);
        }
        if (method is null || password is null || host is null || !ValidPort(port)) return (null, name);
        method = WebUtility.UrlDecode(method);
        password = WebUtility.UrlDecode(password);
        if (method.Length == 0 || password.Length == 0 || !SsCiphers.Contains(method) || !ValidCipherPassword(method, password)) return (null, name);
        var d = new Dictionary<string, object>
        {
            ["type"] = "ss",
            ["server"] = host,
            ["port"] = port!.Value,
            ["cipher"] = method,
            ["password"] = password,
        };
        if (q.TryGetValue("plugin", out var plugin) && plugin.Length > 0)
        {
            var parts = plugin.Split(';');
            if (parts.Length > 0 && parts[0].Length > 0)
            {
                d["plugin"] = parts[0];
                if (parts.Length > 1)
                {
                    var opts = ParsePluginOpts(parts[0], parts[1..]);
                    if (opts.Count > 0) d["plugin-opts"] = opts;
                }
            }
        }
        return (d, name);
    }

    static Dictionary<string, object> ParsePluginOpts(string plugin, string[] opts)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var o in opts)
        {
            var kv = o.Split('=', 2);
            if (kv.Length == 2) d[kv[0].Trim()] = kv[1].Trim();
            else if (o.Trim() is "tls" or "mux") d[o.Trim()] = "true";
        }
        var result = new Dictionary<string, object>();
        if (plugin.Contains("v2ray-plugin"))
        {
            result["mode"] = d.GetValueOrDefault("mode", "websocket");
            if (d.TryGetValue("tls", out _)) result["tls"] = true;
            if (d.TryGetValue("host", out var host)) result["host"] = host;
            if (d.TryGetValue("path", out var path)) result["path"] = path;
        }
        else if (plugin.Contains("obfs-local"))
        {
            if (d.TryGetValue("obfs", out var obfs)) result["mode"] = obfs;
            if (d.TryGetValue("obfs-host", out var obfsHost)) result["host"] = obfsHost;
        }
        return result;
    }

    /// <summary>SSR 降级成 SS：只取 method / password / server / port，protocol 与 obfs 丢掉。
    /// 两种写法都支持：整体 Base64，或明文 host:port:protocol:method:obfs:password/?参数。</summary>
    static (Dictionary<string, object>? proxy, string name) ParseSsr(string rest)
    {
        var (main, name) = SplitFragment(rest);
        string core;
        Dictionary<string, string> q;
        var decoded = B64Decode(main);
        if (decoded is not null)
        {
            // 整体 Base64：host:port:protocol:method:obfs:password/?obfsparam=..&remarks=..
            var slash = decoded.IndexOf('/');
            core = slash < 0 ? decoded : decoded[..slash];
            q = slash < 0
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : ParseQuery(decoded[(slash + 1)..].TrimStart('?'));
        }
        else
        {
            // 明文：host:port:protocol:method:obfs:password/?obfsparam=..&remarks=..
            var qi = main.IndexOf('?');
            var left = qi < 0 ? main : main[..qi];
            core = left.TrimEnd('/');
            q = qi < 0
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : ParseQuery(main[(qi + 1)..]);
        }
        var parts = core.Split(':');
        if (parts.Length < 6) return (null, name);
        var host = parts[0];
        if (!int.TryParse(parts[1], out var port)) return (null, name);
        var method = parts[3];
        var password = B64Decode(parts[5]) ?? parts[5];
        if (host.Length == 0 || method.Length == 0 || password.Length == 0 || !ValidPort(port) || !SsCiphers.Contains(method) || !ValidCipherPassword(method, password)) return (null, name);
        if (q.TryGetValue("remarks", out var remarks) && remarks.Length > 0)
        {
            var rn = B64Decode(remarks);
            if (rn is { Length: > 0 }) name = rn;
        }
        var d = new Dictionary<string, object>
        {
            ["type"] = "ss",
            ["server"] = host,
            ["port"] = port,
            ["cipher"] = method,
            ["password"] = password,
        };
        return (d, name);
    }

    static (Dictionary<string, object>? proxy, string name) ParseHysteria2(string rest)
    {
        var (main, name) = SplitFragment(rest);
        var (beforeQ, query) = SplitQuery(main);
        var q = ParseQuery(query);
        var at = beforeQ.LastIndexOf('@');
        if (at < 0) return (null, name);
        var userinfo = WebUtility.UrlDecode(beforeQ[..at]);
        var colon = userinfo.IndexOf(':');
        var password = colon < 0 ? userinfo : userinfo[(colon + 1)..];
        var (host, port) = ParseHostPort(beforeQ[(at + 1)..]);
        if (host is null || !ValidPort(port) || password.Length == 0) return (null, name);
        var d = new Dictionary<string, object>
        {
            ["type"] = "hysteria2",
            ["server"] = host,
            ["port"] = port!.Value,
            ["password"] = password,
        };
        if (q.TryGetValue("sni", out var sni) && sni.Length > 0) d["sni"] = sni;
        if (q.TryGetValue("insecure", out var ins) && ins is "1" or "true") d["skip-cert-verify"] = true;
        if (q.TryGetValue("obfs-password", out var obfsPw) && obfsPw.Length > 0)
        {
            d["obfs"] = "salamander";
            d["obfs-password"] = obfsPw;
        }
        else if (q.TryGetValue("obfs", out var obfs) && obfs == "salamander") d["obfs"] = obfs;
        if (q.TryGetValue("alpn", out var alpn) && alpn.Length > 0) d["alpn"] = SplitList(alpn);
        return (d, name);
    }

    static (Dictionary<string, object>? proxy, string name) ParseHysteria(string rest)
    {
        var (main, name) = SplitFragment(rest);
        var (beforeQ, query) = SplitQuery(main);
        var q = ParseQuery(query);
        var (host, port) = ParseHostPort(beforeQ);
        if (host is null || !ValidPort(port)) return (null, name);
        var auth = "";
        if (q.TryGetValue("authStr", out var authStr) && authStr.Length > 0) auth = authStr;
        else if (q.TryGetValue("auth", out var authB64) && authB64.Length > 0) auth = authB64;
        if (auth.Length == 0) return (null, name);
        var d = new Dictionary<string, object>
        {
            ["type"] = "hysteria",
            ["server"] = host,
            ["port"] = port!.Value,
            ["auth_str"] = auth,
        };
        if (q.TryGetValue("protocol", out var proto) && proto.Length > 0) d["protocol"] = proto;
        if (q.TryGetValue("sni", out var sni) && sni.Length > 0) d["sni"] = sni;
        if (q.TryGetValue("insecure", out var ins) && ins is "1" or "true") d["skip-cert-verify"] = true;
        if (q.TryGetValue("alpn", out var alpn) && alpn.Length > 0) d["alpn"] = SplitList(alpn);
        if (q.TryGetValue("obfs", out var obfs) && obfs.Length > 0) d["obfs"] = obfs;
        return (d, name);
    }

    static (Dictionary<string, object>? proxy, string name) ParseTuic(string rest)
    {
        var (main, name) = SplitFragment(rest);
        var (beforeQ, query) = SplitQuery(main);
        var q = ParseQuery(query);
        var at = beforeQ.LastIndexOf('@');
        if (at < 0) return (null, name);
        var userinfo = WebUtility.UrlDecode(beforeQ[..at]);
        var colon = userinfo.IndexOf(':');
        if (colon < 0) return (null, name); // tuic v5 需要 uuid:password
        var uuid = NormalizeUuid(userinfo[..colon]);
        var password = userinfo[(colon + 1)..];
        var (host, port) = ParseHostPort(beforeQ[(at + 1)..]);
        if (host is null || !ValidPort(port) || uuid is null || password.Length == 0) return (null, name);
        var d = new Dictionary<string, object>
        {
            ["type"] = "tuic",
            ["server"] = host,
            ["port"] = port!.Value,
            ["uuid"] = uuid,
            ["password"] = password,
        };
        if (q.TryGetValue("alpn", out var alpn) && alpn.Length > 0) d["alpn"] = SplitList(alpn);
        if (q.TryGetValue("sni", out var sni) && sni.Length > 0) d["sni"] = sni;
        if (q.TryGetValue("allow_insecure", out var ai) && ai is "1" or "true") d["skip-cert-verify"] = true;
        if (q.TryGetValue("congestion_control", out var cc) && cc is "cubic" or "new_reno" or "bbr") d["congestion-controller"] = cc;
        if (q.TryGetValue("udp_relay_mode", out var urm) && urm is "native" or "quic") d["udp-relay-mode"] = urm;
        return (d, name);
    }

    static (Dictionary<string, object>? proxy, string name) ParseSocks(string rest)
    {
        var (main, name) = SplitFragment(rest);
        var (beforeQ, _) = SplitQuery(main);
        string? user = null, pass = null;
        var target = beforeQ;
        var at = beforeQ.LastIndexOf('@');
        if (at >= 0)
        {
            var userinfo = WebUtility.UrlDecode(beforeQ[..at]);
            var colon = userinfo.IndexOf(':');
            if (colon >= 0) { user = userinfo[..colon]; pass = userinfo[(colon + 1)..]; }
            else user = userinfo;
            target = beforeQ[(at + 1)..];
        }
        var (host, port) = ParseHostPort(target);
        if (host is null || !ValidPort(port)) return (null, name);
        var d = new Dictionary<string, object>
        {
            ["type"] = "socks5",
            ["server"] = host,
            ["port"] = port!.Value,
        };
        if (user is { Length: > 0 }) d["username"] = user;
        if (pass is { Length: > 0 }) d["password"] = pass;
        return (d, name);
    }

    static (Dictionary<string, object>? proxy, string name) ParseHttp(string rest, bool tls)
    {
        var (proxy, name) = ParseSocks(rest);
        if (proxy is null) return (null, name);
        proxy["type"] = "http";
        if (tls) proxy["tls"] = true;
        return (proxy, name);
    }
}
