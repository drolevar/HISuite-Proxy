using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ProxyDiagnostics
{
    public static class Log
    {
        private const int RecordLimit = 65536, BodyLimit = 65536;
        private static readonly object Gate = new object();
        private static readonly AsyncLocal<string> Active = new AsyncLocal<string>();
        private static readonly ConditionalWeakTable<object, Flow> Flows = new ConditionalWeakTable<object, Flow>();
        private static readonly HashSet<string> Allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "status","state","result","code","error","errorcode","message","msg","description","desc","reason",
            "packagetype","os","osversion","version","versionname","versionid","versioncode","versionnumber","versiontype",
            "hotaver","hotaversion","cversion","dversion","romid","romversion","romname","packageid","packageids",
            "name","model","product","productname","board","vendor","country","vendorcountry","region","cust","preload","base",
            "package","packagename","filename","url","uri","downloadurl","packageurl","baseurl","custurl","preloadurl",
            "baseversion","custversion","preloadversion","baseid","custid","preloadid","size","filesize","packagesize",
            "md5","sha256","filehash","packagehash","approved","isapproved","available","isavailable","compatible","iscompatible",
            "updatetype","updatemode","rollback","isrollback","downgrade","isdowngrade","recovery","isrecovery","isfull",
            "channel","channelid","build","buildnumber","type","id","count","total","totalcount","length",
            "newmethod","lastrequestismine","selectedradiochecked","overridedtherequest","requesttype","asyncstate",
            "method","hasbody","contenttype","contentlength","proxyport","checked","selected",
            "ret","retcode","returncode","returndesc","errcode","success","issuccess","valid","isvalid","canrollback",
            "targetversion","firmwareversion","firmwareid","verid","ver","querytype","requestmethod","latestversion","currversion","currentversion","previousversion",
            "devicename","firmware","productbaseversion","authvendor","authcountry","hisuiteversion",
            "versions","versionlist","packageversions","romversions","firmwareversions","urllist","packageurllist","rooturl","romurl","downloadpath",
            "versionurl","versionurls","basepackageurl","custpackageurl","preloadpackageurl","hasfullpackage","hasreloadedpackage","hascustpackage","producttype",
            "statuscode","httpstatus","range","contentrange"
        };
        private static readonly string[] Controls = { "textBox1","textBox2","textBox4","textBox5","textBox6","textBox7","textBox8","textBox9","textBox10","textBox11" };
        private static readonly string[] ControlNames = { "baseUrl","baseVersion","preloadUrl","preloadVersion","custVersion","custUrl","versionType","baseId","custId","preloadId" };
        private static long flowNumber, sequence, written;
        private static int part;
        private static StreamWriter output;
        private static string prefix, filePath;
        private static bool disabled;
        private sealed class Flow { internal readonly string Id = NewFlow(); }

        public static string CurrentLogPath { get { try { lock (Gate) { Initialize(); return filePath; } } catch { return null; } } }
        public static string CurrentFlowId { get { try { return Active.Value; } catch { return null; } } }
        public static void Event(string eventName, string details) { try { Append(eventName, new JObject { ["details"] = Body(details) }); } catch { } }
        public static string ObserveRequest(string stage, string url, string body)
        {
            try { Append("request", new JObject { ["stage"] = Text(stage,256), ["url"] = Url(url), ["bodyLength"] = body == null ? 0 : body.Length, ["body"] = Body(body) }); } catch { }
            return body;
        }
        public static string ObserveResponse(string stage, string url, string body)
        {
            try { Append("response", new JObject { ["stage"] = Text(stage,256), ["url"] = Url(url), ["bodyLength"] = body == null ? 0 : body.Length, ["body"] = Body(body) }); } catch { }
            return body;
        }
        public static void ObserveException(string stage, Exception error) { try { Append("exception", new JObject { ["stage"] = Text(stage,256), ["error"] = Error(error,0) }); } catch { } }
        public static void Caught(object error, string stage)
        {
            try { if (error is Exception) ObserveException(stage,(Exception)error); else Append("caught.object",new JObject { ["stage"] = Text(stage,256), ["type"] = error == null ? null : error.GetType().FullName }); } catch { }
        }
        public static string UploadString(WebClient client, string address, string method, string body)
        {
            ObserveRequest("webclient.upload",address,body);
            try { return ObserveResponse("webclient.response",address,client.UploadString(address,method,body)); }
            catch (Exception error) { ObserveException("webclient.error",error); throw; }
        }
        public static string UploadString(WebClient client, string address, string body)
        {
            ObserveRequest("webclient.upload",address,body);
            try { return ObserveResponse("webclient.response",address,client.UploadString(address,body)); }
            catch (Exception error) { ObserveException("webclient.error",error); throw; }
        }

        public static void Checkpoint(object context, string stage)
        {
            try
            {
                object session = FindSession(context,0);
                Active.Value = session == null ? (Active.Value ?? NewFlow()) : Flows.GetValue(session,key => new Flow()).Id;
                JObject record = new JObject { ["stage"] = Text(stage,256), ["contextType"] = context == null ? null : context.GetType().FullName };
                JObject state = new JObject();
                foreach (FieldInfo field in Fields(context))
                {
                    string name = FieldName(field.Name);
                    if (name == "asyncState" || name.Equals("requestType",StringComparison.OrdinalIgnoreCase) || name.Equals("overridedTheRequest",StringComparison.OrdinalIgnoreCase))
                        state[name] = Snapshot(Read(field,context),0);
                    else if (name.Equals("reqeustURL",StringComparison.OrdinalIgnoreCase) || name.Equals("requestURL",StringComparison.OrdinalIgnoreCase))
                        state[name] = Url(Read(field,context) as string);
                }
                record["state"] = state;
                object form = FindForm(context);
                if (form != null) record["selection"] = FormSnapshot(form);
                if (session != null)
                {
                    object request = Property(Property(session,"HttpClient"),"Request");
                    record["request"] = new JObject { ["url"] = Url(Property(request,"Url") as string), ["method"] = Text(Convert.ToString(Property(request,"Method")),32),
                        ["hasBody"] = Snapshot(Property(request,"HasBody"),0), ["contentLength"] = Snapshot(Property(request,"ContentLength"),0),
                        ["range"] = Header(request,"Range") };
                    object response = Property(Property(session,"HttpClient"),"Response");
                    if (response != null) record["response"] = new JObject
                    {
                        ["statusCode"] = Snapshot(Property(response,"StatusCode"),0),
                        ["contentLength"] = Snapshot(Property(response,"ContentLength"),0),
                        ["contentRange"] = Header(response,"Content-Range")
                    };
                }
                Append("checkpoint",record);
            }
            catch { }
        }
        private static JObject FormSnapshot(object form)
        {
            JObject record = new JObject();
            foreach (FieldInfo field in Fields(form))
            {
                string name = FieldName(field.Name);
                if (name.Equals("targetRomData",StringComparison.OrdinalIgnoreCase))
                    record[name] = ProfileSnapshot(Read(field,form));
                else if (name.Equals("newMethod",StringComparison.OrdinalIgnoreCase) || name.Equals("lastrequestIsMine",StringComparison.OrdinalIgnoreCase) || name.Equals("selectedRadioChecked",StringComparison.OrdinalIgnoreCase))
                    record[name] = Snapshot(Read(field,form),0);
                else if (name.StartsWith("checkBox",StringComparison.OrdinalIgnoreCase) || name.StartsWith("radioButton",StringComparison.OrdinalIgnoreCase))
                    record[name] = Snapshot(Property(Read(field,form),"Checked"),0);
                else
                {
                    int index = Array.FindIndex(Controls,item => item.Equals(name,StringComparison.OrdinalIgnoreCase) || item.Replace("textBox","txt").Equals(name,StringComparison.OrdinalIgnoreCase));
                    if (index >= 0)
                    {
                        string text = Convert.ToString(Property(Read(field,form),"Text"));
                        record[ControlNames[index]] = ControlNames[index].EndsWith("Url",StringComparison.Ordinal) ? Url(text) : Text(text,512);
                    }
                }
            }
            return record;
        }
        private static JToken ProfileSnapshot(object value)
        {
            JArray row=value as JArray;
            if(row==null)return Snapshot(value,0);
            if(row.Count!=6)return new JObject{["catalogSchema"]="invalid row",["fieldCount"]=row.Count};
            JArray sources=new JArray();
            JArray triples=row[3] as JArray;
            if(triples!=null)
            {
                int count=0;
                foreach(JToken triple in triples){if(++count>32){sources.Add("<more source triples>");break;}sources.Add(ProfileTriple(triple));}
            }
            else sources.Add("<redacted>");
            return new JObject
            {
                ["catalogSchema"]="v2-six-field-row",
                ["model"]=ProfileText(row[0],@"\A[A-Z0-9]{2,24}(?:-[A-Z0-9]{1,24}){1,3}\z"),
                ["region"]=ProfileText(row[1],@"\AC[0-9]{2,4}\s*-\s*[A-Za-z0-9][A-Za-z0-9 _-]{0,100}\z"),
                ["targetPackages"]=ProfileTriple(row[2]),
                ["sourceVersionTriples"]=sources,
                ["requestType"]=ProfileNumber(row[4]),
                ["queryMode"]=ProfileNumber(row[5])
            };
        }
        private static JToken ProfileTriple(JToken value)
        {
            JArray triple=value as JArray;
            if(triple==null||triple.Count!=3)return new JValue("<redacted>");
            JArray result=new JArray();
            foreach(JToken item in triple)
                result.Add(ProfileText(item,@"\A[A-Z0-9]{2,24}(?:-[A-Z0-9]{1,24}){1,4} [0-9]{1,3}(?:\.[0-9]{1,4}){2,4}(?:\([A-Z0-9_. -]{1,100}\))?\z"));
            return result;
        }
        private static JToken ProfileText(JToken value,string pattern)
        {
            if(value==null||value.Type!=JTokenType.String)return new JValue("<redacted>");
            string text=(string)value;
            if(text==null||text.Length>512||Regex.IsMatch(text,@"[^\x20-\x7e]|\d{11,}")||!Regex.IsMatch(text,pattern))
                return new JValue("<redacted>");
            return new JValue(text);
        }
        private static JToken ProfileNumber(JToken value)
        {
            if(value==null||value.Type!=JTokenType.Integer)return new JValue("<redacted>");
            long number=(long)value;
            return number>=0&&number<=16?new JValue(number):new JValue("<redacted>");
        }
        private static object FindSession(object value, int depth)
        {
            if (value == null || depth > 2) return null;
            for (Type type = value.GetType(); type != null; type = type.BaseType)
                if (type.Name == "SessionEventArgs" && type.Namespace == "Titanium.Web.Proxy.EventArguments") return value;
            foreach (FieldInfo field in Fields(value))
                if (FieldName(field.Name) == "e" || FieldName(field.Name) == "session" || field.Name.StartsWith("<>8__",StringComparison.Ordinal))
                {
                    object found = FindSession(Read(field,value),depth+1);
                    if (found != null) return found;
                }
            return null;
        }
        private static object FindForm(object value)
        {
            if (value == null) return null;
            if (value.GetType().Name == "Form1") return value;
            foreach (FieldInfo field in Fields(value))
                if (field.Name == "<>4__this" || field.Name == "form" || field.Name.StartsWith("<>8__",StringComparison.Ordinal))
                {
                    object candidate = Read(field,value);
                    if (candidate == null) continue;
                    if (candidate.GetType().Name == "Form1") return candidate;
                    foreach (FieldInfo nested in Fields(candidate))
                        if (nested.Name == "<>4__this" || nested.Name == "form")
                        {
                            object form = Read(nested,candidate);
                            if (form != null && form.GetType().Name == "Form1") return form;
                        }
                }
            return null;
        }
        private static IEnumerable<FieldInfo> Fields(object value)
        {
            if (value == null) yield break;
            int count=0;
            foreach (FieldInfo field in value.GetType().GetFields(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic)) { if (++count>128) yield break; yield return field; }
        }
        private static string FieldName(string name) { if (name=="<>1__state") return "asyncState"; int end=name.IndexOf('>'); return name.StartsWith("<",StringComparison.Ordinal)&&end>1 ? name.Substring(1,end-1) : name; }
        private static object Read(FieldInfo field,object value) { try { return field.GetValue(value); } catch { return null; } }
        private static object Property(object value,string name) { try { return value==null?null:value.GetType().GetProperty(name,BindingFlags.Instance|BindingFlags.Public).GetValue(value,null); } catch { return null; } }
        private static string Header(object message,string name)
        {
            try
            {
                object headers=Property(message,"Headers");
                IDictionary values=(Property(headers,"Headers")??headers) as IDictionary;
                if(values!=null)
                {
                    int count=0;
                    foreach(DictionaryEntry entry in values)
                    {
                        if(++count>128)break;
                        if(String.Equals(entry.Key as string,name,StringComparison.OrdinalIgnoreCase))
                            return Text(Convert.ToString(Property(entry.Value,"Value")??entry.Value as string),256);
                    }
                }
                MethodInfo getter=headers==null?null:headers.GetType().GetMethod("GetHeaderValue",new[]{typeof(string)});
                return getter==null?null:Text(getter.Invoke(headers,new object[]{name}) as string,256);
            }
            catch{return null;}
        }

        private static JToken Snapshot(object value,int depth)
        {
            if (value==null) return JValue.CreateNull();
            if (depth>12) return new JValue("<depth limit>");
            if (value is JToken) return Clean((JToken)value,null,depth);
            if (value is string) return Body((string)value);
            if (value is bool||value is int||value is uint||value is long||value is ulong||value is short||value is byte||value is double||value is decimal) return new JValue(value);
            if (value.GetType().IsEnum) return new JValue(Text(value.ToString(),128));
            IDictionary map=value as IDictionary;
            if (map!=null)
            {
                JObject result=new JObject(); int count=0;
                foreach(DictionaryEntry item in map) { if(++count>32){result["_truncated"]=true;break;} string name=Convert.ToString(item.Key); result[Text(name,128)]=Secret(name)?new JValue("<redacted>"):Clean(Snapshot(item.Value,depth+1),name,depth+1); }
                return result;
            }
            IEnumerable items=value as IEnumerable;
            if(items!=null) { JArray result=new JArray(); int count=0; foreach(object item in items){if(++count>16){result.Add("<more items>");break;}result.Add(Snapshot(item,depth+1));}return result; }
            JObject fields=new JObject(); int n=0;
            foreach(FieldInfo field in Fields(value)){if(++n>32){fields["_truncated"]=true;break;}string name=FieldName(field.Name);fields[Text(name,128)]=Secret(name)?new JValue("<redacted>"):Allowed.Contains(Normalize(name))?Snapshot(Read(field,value),depth+1):new JValue("<not allowlisted>");}
            return fields;
        }
        private static JToken Body(string body)
        {
            if(body==null)return JValue.CreateNull();
            string start=body.TrimStart();
            if(start.StartsWith("{",StringComparison.Ordinal)||start.StartsWith("[",StringComparison.Ordinal))
            {
                if(body.Length>BodyLimit)return new JObject{["format"]="oversized-json",["length"]=body.Length,["values"]="<omitted>"};
                try{using(StringReader input=new StringReader(body))using(JsonTextReader reader=new JsonTextReader(input){MaxDepth=12,DateParseHandling=DateParseHandling.None})return Clean(JToken.ReadFrom(reader),null,0);}
                catch{return new JObject{["format"]="invalid-json",["length"]=body.Length,["values"]="<omitted>"};}
            }
            return new JObject{["format"]="text",["length"]=body.Length,["preview"]=Text(body,4096),["truncated"]=body.Length>4096};
        }
        private static JToken Clean(JToken token,string name,int depth)
        {
            if(Secret(name))return new JValue("<redacted>");
            if(depth>12)return new JValue("<depth limit>");
            JObject map=token as JObject;
            if(map!=null){JObject result=new JObject();int count=0;foreach(JProperty p in map.Properties()){if(++count>48){result["_truncated"]=true;break;}result[Text(p.Name,128)]=Clean(p.Value,p.Name,depth+1);}return result;}
            JArray array=token as JArray;
            if(array!=null){JArray result=new JArray();int count=0;foreach(JToken item in array){if(++count>16){result.Add("<more items>");break;}result.Add(Clean(item,name,depth+1));}return result;}
            if(token.Type==JTokenType.Null)return JValue.CreateNull();
            if(!Allowed.Contains(Normalize(name)))return new JValue("<not allowlisted>");
            string raw=token.Type==JTokenType.String?(string)token:token.ToString(Formatting.None);
            string key=Normalize(name);
            if(key.Contains("url")||key=="uri")return new JValue(Url(raw));
            if((key=="md5"||key=="sha256"||key.EndsWith("hash",StringComparison.Ordinal))&&Regex.IsMatch(raw??"",@"\A[0-9a-fA-F]{32,64}\z"))return new JValue(raw);
            if(token.Type==JTokenType.Boolean||token.Type==JTokenType.Integer||token.Type==JTokenType.Float)return token.DeepClone();
            return new JValue(Text(raw,1024));
        }
        private static string Normalize(string value){return Regex.Replace(value??"","[^a-zA-Z0-9]","").ToLowerInvariant();}
        private static bool Secret(string value)
        {
            string key=Normalize(value);
            // These release fields describe product/region, not authentication credentials.
            if(key=="authvendor"||key=="authcountry")return false;
            return key.Contains("deviceid")||key.Contains("deviceidentifier")||key.Contains("imei")||key.Contains("meid")||key.Contains("imsi")||key.Contains("iccid")||key.Contains("serial")||key=="sn"||key.Contains("androidid")||key.Contains("udid")||key.Contains("oaid")||key.Contains("aaid")||key.Contains("cert")||key.Contains("attest")||key.Contains("token")||key.Contains("signature")||key=="sign"||key.Contains("cookie")||key.Contains("auth")||key.Contains("password")||key.Contains("secret")||key.Contains("nonce")||key.EndsWith("key",StringComparison.Ordinal)||key=="sessionid";
        }
        private static string Url(string value)
        {
            if(String.IsNullOrWhiteSpace(value))return value;
            try
            {
                Uri uri;
                if(!Uri.TryCreate(value,UriKind.Absolute,out uri)||(uri.Scheme!="http"&&uri.Scheme!="https"))return "<non-http address>";
                string pathname=Regex.Replace(uri.AbsolutePath,@"\d{11,}","<redacted-digits>");
                pathname=Regex.Replace(pathname,@"(?i)/(deviceid|serial|imei|token|auth)/[^/]+","/$1/<redacted>");
                string query=uri.Query;
                if(query.Length>1){List<string> keys=new List<string>();foreach(string item in query.Substring(1).Split('&')){if(keys.Count>=16)break;keys.Add(Text(Uri.UnescapeDataString(item.Split('=')[0]),64)+"=<redacted>");}query="?"+String.Join("&",keys);}
                return uri.Scheme+"://"+uri.Host+(uri.IsDefaultPort?"":":"+uri.Port)+pathname+query;
            }
            catch{return "<invalid address>";}
        }
        private static string Text(string value,int limit)
        {
            if(value==null)return null;
            if(value.Length>limit)value=value.Substring(0,limit)+"<truncated>";
            value=Regex.Replace(value,@"(?im)\b(authorization|proxy-authorization|cookie|set-cookie)\s*:[^\r\n]*","$1: <redacted>");
            value=Regex.Replace(value,@"(?i)\bBearer\s+[A-Za-z0-9._~+/=-]+","Bearer <redacted>");
            value=Regex.Replace(value,@"(?i)([""']?(?:device[ _-]?(?:id|identifier)|imei|meid|imsi|iccid|serial(?:[ _-]?number)?|sn|(?:access|update|refresh)?[ _-]?token|sign(?:ature)?|password|secret|nonce|cert(?:ificate)?|attest(?:ation)?|auth(?:orization)?|session[ _-]?id|(?:private|public|signing)[ _-]?key)[""']?\s*[:=]\s*)(?:""(?:\\.|[^""])*""|'[^']*'|[^,\s;}\]]+)","$1<redacted>");
            value=Regex.Replace(value,@"https?://[^\s""'<>]+",match=>Url(match.Value),RegexOptions.IgnoreCase);
            value=Regex.Replace(value,@"\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b","<redacted-email>",RegexOptions.IgnoreCase);
            value=Regex.Replace(value,@"\d{11,}","<redacted-digits>");
            return Regex.Replace(value,@"[A-Za-z0-9+/_=-]{40,}","<redacted-opaque>");
        }
        private static JObject Error(Exception error,int depth)
        {
            if(error==null)return new JObject{["type"]=null};
            JObject result=new JObject{["type"]=error.GetType().FullName,["hresult"]=error.HResult,["message"]=Text(error.Message,4096),["stack"]=Text(error.StackTrace,12000)};
            WebException web=error as WebException;
            if(web!=null)
            {
                result["webExceptionStatus"]=web.Status.ToString();
                HttpWebResponse http=web.Response as HttpWebResponse;
                if(http!=null)result["httpStatus"]=(int)http.StatusCode;
                if(web.Response!=null)
                {
                    try
                    {
                        Stream stream=web.Response.GetResponseStream();
                        bool capture=Environment.GetEnvironmentVariable("HISUITE_PROXY_CAPTURE_ERROR_BODY")=="1";
                        if(stream==null)result["responseBodyUnavailable"]="no response stream";
                        else if(!stream.CanSeek&&!capture)result["responseBodyUnavailable"]="non-seekable response preserved";
                        else
                        {
                            long position=stream.CanSeek?stream.Position:0;int timeout=stream.CanTimeout?stream.ReadTimeout:0;
                            byte[] bytes=new byte[BodyLimit];int used=0;
                            try{if(stream.CanTimeout)stream.ReadTimeout=1000;while(used<bytes.Length){int count=stream.Read(bytes,used,bytes.Length-used);if(count==0)break;used+=count;}result["responseBody"]=Body(Encoding.UTF8.GetString(bytes,0,used));result["responseBodyReadBytes"]=used;result["responseBodyMayBeTruncated"]=used==bytes.Length;if(!stream.CanSeek)result["consumed_error_body"]=true;}
                            finally{if(stream.CanSeek)stream.Position=position;if(stream.CanTimeout)stream.ReadTimeout=timeout;}
                        }
                    }
                    catch(Exception captureError){result["responseBodyUnavailable"]=captureError.GetType().FullName;}
                }
            }
            if(depth<2&&error.InnerException!=null)result["inner"]=Error(error.InnerException,depth+1);
            return result;
        }
        private static string NewFlow(){return "flow-"+Interlocked.Increment(ref flowNumber).ToString("D6");}
        private static void Append(string name,JObject data)
        {
            try{lock(Gate){if(!Initialize())return;if(Active.Value==null)Active.Value=NewFlow();Write(Text(name,128),data,Active.Value);}}catch{}
        }
        private static bool Initialize()
        {
            if(disabled)return false;
            if(output!=null)return true;
            try
            {
                string directory=Environment.GetEnvironmentVariable("HISUITE_PROXY_LOG_DIR");
                if(String.IsNullOrWhiteSpace(directory))directory=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"logs");
                directory=Path.GetFullPath(directory);Directory.CreateDirectory(directory);
                prefix=Path.Combine(directory,DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ")+"-"+Process.GetCurrentProcess().Id+"-"+Guid.NewGuid().ToString("N").Substring(0,8));
                Open();
                Assembly entry=Assembly.GetEntryAssembly();
                Write("session.start",new JObject{["application"]=entry==null?null:entry.GetName().Name,["applicationVersion"]=entry==null?null:entry.GetName().Version.ToString(),["loggerVersion"]=typeof(Log).Assembly.GetName().Version.ToString(),["recordLimitBytes"]=RecordLimit,["fileLimitBytes"]=16777216,["fileLimitCount"]=8,["captureNonSeekableErrorBody"]=Environment.GetEnvironmentVariable("HISUITE_PROXY_CAPTURE_ERROR_BODY")=="1"},null);
                return true;
            }
            catch{disabled=true;return false;}
        }
        private static void Open(){filePath=prefix+"-"+(++part).ToString("D2")+".jsonl";output=new StreamWriter(new FileStream(filePath,FileMode.CreateNew,FileAccess.Write,FileShare.ReadWrite),new UTF8Encoding(false),4096){AutoFlush=true};written=0;}
        private static void Write(string name,JObject data,string flow)
        {
            JObject record=new JObject{["timestampUtc"]=DateTime.UtcNow.ToString("o"),["sequence"]=++sequence,["processId"]=Process.GetCurrentProcess().Id,["threadId"]=Thread.CurrentThread.ManagedThreadId,["flowId"]=flow,["event"]=name,["data"]=data};
            string line=record.ToString(Formatting.None);
            if(Encoding.UTF8.GetByteCount(line)>RecordLimit-2){record["data"]=new JObject{["recordTruncated"]=true,["sanitizedPreview"]=line.Substring(0,Math.Min(line.Length,8000))};line=record.ToString(Formatting.None);}
            int bytes=Encoding.UTF8.GetByteCount(line)+2;
            if(written+bytes>16777216){output.Dispose();if(part>=8){disabled=true;return;}Open();}
            output.WriteLine(line);written+=bytes;
        }
    }
}
