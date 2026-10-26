using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Serialization;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using ProxyDiagnostics;
using Titanium.Web.Proxy.EventArguments;

internal static class LoggerTests
{
    private static int pass, fail;
    public static int Main(string[] args)
    {
        string root = Path.GetFullPath(args[0]);
        Directory.CreateDirectory(root);
        if (args.Length > 1 && args[1] == "unwritable")
        {
            string blocked = Path.Combine(root,"not-a-directory");
            File.WriteAllText(blocked,"fixture");
            Environment.SetEnvironmentVariable("HISUITE_PROXY_LOG_DIR",blocked);
            string body = "{\"status\":0}";
            if (!Object.ReferenceEquals(body,Log.ObserveRequest("disabled","https://fixture.invalid/",body))) return 1;
            Log.Event("disabled","anything"); Log.Checkpoint(null,"disabled"); Log.Caught(new object(),"disabled");
            using (FakeClient client = new FakeClient("ok",null)) if (Log.UploadString(client,"https://fixture.invalid/","body") != "ok") return 1;
            Console.WriteLine("PASS: unwritable log sink does not change wrapper behavior");
            return 0;
        }
        Environment.SetEnvironmentVariable("HISUITE_PROXY_LOG_DIR",root);
        if(args.Length>1&&args[1]=="privacy-only")
        {
            Test("recursive JSON, URLs, text, and short secret aliases are redacted",Privacy);
            Test("deep firmware metadata retained and response metadata is allowlisted",DeepResponseMetadata);
            Console.WriteLine("RESULT: "+pass+" passed, "+fail+" failed. Log: "+Log.CurrentLogPath);
            return fail==0?0:1;
        }
        if(args.Length>1&&args[1]=="profile-only")
        {
            Test("catalog row snapshot retains public profile fields and blocks secret intrusion",CatalogProfile);
            Console.WriteLine("RESULT: "+pass+" passed, "+fail+" failed. Log: "+Log.CurrentLogPath);
            return fail==0?0:1;
        }
        Test("recursive JSON, URLs, and text are redacted",Privacy);
        Test("unknown scalars retain shape without values",UnknownShape);
        Test("observe wrappers preserve body references and null",BodyIdentity);
        Test("request correlation survives display class and async flow",Correlation);
        Test("explicit form selection snapshot excludes other controls",Selection);
        Test("deep firmware metadata retained and response metadata is allowlisted",DeepResponseMetadata);
        Test("catalog row snapshot retains public profile fields and blocks secret intrusion",CatalogProfile);
        Test("WebClient success preserves method, body, and response",UploadSuccess);
        Test("WebClient error rethrows identical exception and restores error stream",UploadFailure);
        Test("non-seekable error stream preserved by default, capture opt-in is explicit",NonSeekable);
        Test("caught non-exception logs only object type",CaughtObject);
        Test("records remain valid, sequential, thread-safe, and bounded",Concurrency);
        Console.WriteLine("RESULT: "+pass+" passed, "+fail+" failed. Log: "+Log.CurrentLogPath);
        return fail == 0 ? 0 : 1;
    }
    private static void Test(string name,Action action){try{action();pass++;Console.WriteLine("PASS: "+name);}catch(Exception e){fail++;Console.WriteLine("FAIL: "+name+": "+e);}}
    private static void Assert(bool condition,string message){if(!condition)throw new Exception(message);}
    private static JObject[] Records(){return Content().Split(new[]{"\r\n","\n"},StringSplitOptions.RemoveEmptyEntries).Select(JObject.Parse).ToArray();}
    private static string Content(){using(FileStream file=new FileStream(Log.CurrentLogPath,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))using(StreamReader reader=new StreamReader(file))return reader.ReadToEnd();}

    private static void Privacy()
    {
        Log.ObserveRequest("privacy","https://user:URL_PASS_SECRET@fixture.invalid/check?token=URL_TOKEN_SECRET&model=BLA-L29",
            "{\"PackageType\":2,\"status\":0,\"versions\":[{\"version\":\"9.1.0.345\",\"versionId\":\"401403\",\"url\":\"https://cdn.invalid/full/?signature=URL_SIG_SECRET\",\"sha256\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"deviceId\":\"DEVICE_SECRET\",\"IMEI\":\"123456789012345\",\"serialNumber\":\"SERIAL_SECRET\",\"deviceCertificate\":{\"value\":\"CERT_SECRET\"},\"keyAttestation\":\"ATTEST_SECRET\",\"accessToken\":\"TOKEN_SECRET\",\"signature\":\"SIGN_SECRET\",\"cookie\":\"COOKIE_SECRET\",\"authorization\":\"AUTH_SECRET\",\"unknownScalar\":\"UNKNOWN_SECRET\"}]}");
        Log.Event("text","Authorization: Bearer HEADER_SECRET\r\nCookie: COOKIE_HEADER_SECRET; other=SECOND_COOKIE_SECRET\r\nimei=123456789012345\r\ncertificate=TEXT_CERT_SECRET\r\ncert=SHORT_CERT_SECRET\r\nSN=SHORT_SERIAL_SECRET\r\nsign=SHORT_SIGN_SECRET\r\n"+new string('A',300));
        string text=Content();
        foreach(string secret in new[]{"URL_PASS_SECRET","URL_TOKEN_SECRET","URL_SIG_SECRET","DEVICE_SECRET","123456789012345","SERIAL_SECRET","CERT_SECRET","ATTEST_SECRET","TOKEN_SECRET","SIGN_SECRET","COOKIE_SECRET","AUTH_SECRET","UNKNOWN_SECRET","HEADER_SECRET","COOKIE_HEADER_SECRET","SECOND_COOKIE_SECRET","TEXT_CERT_SECRET","SHORT_CERT_SECRET","SHORT_SERIAL_SECRET","SHORT_SIGN_SECRET"})
            Assert(!text.Contains(secret),"Secret value remained: "+secret);
        Assert(text.Contains("9.1.0.345")&&text.Contains("401403"),"Firmware fields were lost.");
        Assert(text.Contains(new string('a',64)),"Package hash should be retained.");
        Assert(text.Contains("deviceId")&&text.Contains("<redacted>"),"Secret field shape was lost.");
    }
    private static void UnknownShape(){Log.ObserveResponse("shape",null,"{\"newEnvelope\":{\"status\":3,\"newField\":\"DO_NOT_COPY\",\"items\":[{\"version\":\"v1\"}]},\"commonRules\":{\"DeviceName\":\"BLA-L29\",\"productBaseVersion\":\"102.0.0.225\",\"authVendor\":\"hw/eu\"},\"versionPackageRules\":[{\"rules\":{\"FirmWare\":\"BLA-LGRP2-OVS 9.1.0.345\"}}]}");string text=Content();Assert(text.Contains("newEnvelope")&&text.Contains("newField")&&!text.Contains("DO_NOT_COPY"),"Unknown field shape/value policy failed.");Assert(text.Contains("BLA-L29")&&text.Contains("hw/eu")&&text.Contains("BLA-LGRP2-OVS 9.1.0.345")&&text.Contains("102.0.0.225"),"Release profile metadata was not retained.");}
    private static void BodyIdentity(){string value=new string('x',32);Assert(Object.ReferenceEquals(value,Log.ObserveRequest("identity",null,value)),"Request reference changed.");Assert(Object.ReferenceEquals(value,Log.ObserveResponse("identity",null,value)),"Response reference changed.");Assert(Log.ObserveResponse("null",null,null)==null,"Null changed.");}
    private static object Session(){object result=FormatterServices.GetUninitializedObject(typeof(SessionEventArgs));GC.SuppressFinalize(result);return result;}
    private static void Correlation()
    {
        object first=Session(),second=Session();
        Log.Checkpoint(first,"first");string id=Log.CurrentFlowId;
        Display display=new Display{e=first,form=new Form1()};
        AssemblyBuilder assembly=AppDomain.CurrentDomain.DefineDynamicAssembly(new AssemblyName("LoggerTests.Dynamic"),AssemblyBuilderAccess.Run);
        TypeBuilder builder=assembly.DefineDynamicModule("m").DefineType("FakeAsyncState");
        builder.DefineField("<>8__1",typeof(Display),FieldAttributes.Public);
        builder.DefineField("<>1__state",typeof(int),FieldAttributes.Public);
        builder.DefineField("<reqeustURL>5__2",typeof(string),FieldAttributes.Public);
        Type type=builder.CreateType();object state=Activator.CreateInstance(type);
        type.GetField("<>8__1").SetValue(state,display);type.GetField("<>1__state").SetValue(state,4);type.GetField("<reqeustURL>5__2").SetValue(state,"https://fixture.invalid/check?token=STATE_SECRET");
        Log.Checkpoint(state,"resumed");Assert(Log.CurrentFlowId==id,"State-machine request lost its correlation.");
        Task.Run(()=>Assert(Log.CurrentFlowId==id,"AsyncLocal did not flow.")).GetAwaiter().GetResult();
        Log.Checkpoint(second,"second");Assert(Log.CurrentFlowId!=id,"Different sessions shared an ID.");
        Assert(!Content().Contains("STATE_SECRET"),"State-machine URL leaked its query.");
    }
    private static void Selection(){Log.Checkpoint(new Form1(),"selection");string text=Content();Assert(text.Contains("baseUrl")&&text.Contains("radioButton1")&&text.Contains("overrided")==false,"Missing selection fields.");Assert(!text.Contains("IGNORED_CONTROL_SECRET")&&!text.Contains("TARGET_DEVICE_SECRET"),"Selection leaked excluded state.");}
    private static void DeepResponseMetadata()
    {
        JToken body=new JObject{["FirmWare"]="DEEP-BLA-9.1.0.345",["VersionURL"]="https://cdn.invalid/full/?token=DEEP_SECRET",["deviceId"]="DEEP_DEVICE_SECRET"};
        for(int i=0;i<8;i++)body=new JObject{["nested"+i]=body};
        Log.ObserveResponse("deep",null,body.ToString());
        AssemblyBuilder assembly=AppDomain.CurrentDomain.DefineDynamicAssembly(new AssemblyName("LoggerTests.Response"),AssemblyBuilderAccess.Run);
        TypeBuilder builder=assembly.DefineDynamicModule("m").DefineType("Titanium.Web.Proxy.EventArguments.SessionEventArgs");
        FieldBuilder field=builder.DefineField("client",typeof(FakeHttpClient),FieldAttributes.Public);
        MethodBuilder getter=builder.DefineMethod("get_HttpClient",MethodAttributes.Public|MethodAttributes.SpecialName|MethodAttributes.HideBySig,typeof(FakeHttpClient),Type.EmptyTypes);
        ILGenerator il=getter.GetILGenerator();il.Emit(OpCodes.Ldarg_0);il.Emit(OpCodes.Ldfld,field);il.Emit(OpCodes.Ret);
        PropertyBuilder property=builder.DefineProperty("HttpClient",PropertyAttributes.None,typeof(FakeHttpClient),null);property.SetGetMethod(getter);
        Type type=builder.CreateType();object context=Activator.CreateInstance(type);
        type.GetField("client").SetValue(context,new FakeHttpClient());
        Log.Checkpoint(context,"response.snapshot");
        JObject record=Records().Last(row=>(string)row["data"]["stage"]=="response.snapshot");
        Assert((int)record["data"]["response"]["statusCode"]==206,"Response status missing.");
        Assert((long)record["data"]["response"]["contentLength"]==16,"Response length missing.");
        Assert((string)record["data"]["request"]["range"]=="bytes=0-15"&&(string)record["data"]["response"]["contentRange"]=="bytes 0-15/32768","Range metadata missing.");
        string text=Content();
        Assert(text.Contains("DEEP-BLA-9.1.0.345"),"Depth-eight firmware value lost.");
        Assert(!text.Contains("DEEP_SECRET")&&!text.Contains("DEEP_DEVICE_SECRET")&&!text.Contains("FORBIDDEN_HEADER_SECRET"),"Deep/body/header privacy failed.");
    }
    private static void CatalogProfile()
    {
        JArray triple=new JArray("BLA-LGRP2-OVS 9.1.0.345","BLA-L29-CUST 9.1.0.6(C432)","BLA-L29-PRELOAD 9.1.0.12(C432R1)");
        JArray source=new JArray("BLA-LGRP2-OVS 10.0.0.202","BLA-L29-CUST 10.0.0.9(C432)","BLA-L29-PRELOAD 10.0.0.3(C432R1)");
        JArray row=new JArray("BLA-L29","C432 - hw-eu",triple,new JArray{source},2,1);
        Form1 form=new Form1{targetRomData=row};
        JToken before=row.DeepClone();
        Log.Checkpoint(form,"catalog.profile");
        JObject snapshot=(JObject)Records().Last(record=>(string)record["data"]["stage"]=="catalog.profile")["data"]["selection"]["targetRomData"];
        Assert((string)snapshot["model"]=="BLA-L29"&&(string)snapshot["region"]=="C432 - hw-eu","Model/region lost.");
        Assert((string)snapshot["targetPackages"][0]=="BLA-LGRP2-OVS 9.1.0.345"&&(string)snapshot["sourceVersionTriples"][0][0]=="BLA-LGRP2-OVS 10.0.0.202","Version triples lost.");
        Assert((int)snapshot["requestType"]==2&&(int)snapshot["queryMode"]==1,"Profile flags lost.");
        Assert(JToken.DeepEquals(before,row),"Logger changed the catalog profile.");
        JObject selection=(JObject)Records().Last(record=>(string)record["data"]["stage"]=="catalog.profile")["data"]["selection"];
        Assert((string)selection["versionType"]=="1"&&selection["proxyPort"]==null,"VersionType control was mislabelled as proxy port.");
        row[0]="123456789012345";
        ((JArray)row[2])[1]=new JObject{["deviceId"]="PROFILE_DEVICE_SECRET",["token"]="PROFILE_TOKEN_SECRET"};
        ((JArray)((JArray)row[3])[0])[2]="serial=PROFILE_SERIAL_SECRET";
        row[4]=new JObject{["auth"]="PROFILE_AUTH_SECRET"};
        Log.Checkpoint(form,"catalog.profile.intrusion");
        string text=Content();
        foreach(string secret in new[]{"123456789012345","PROFILE_DEVICE_SECRET","PROFILE_TOKEN_SECRET","PROFILE_SERIAL_SECRET","PROFILE_AUTH_SECRET"})Assert(!text.Contains(secret),"Profile intrusion leaked: "+secret);
    }
    private static void UploadSuccess()
    {
        using(FakeClient client=new FakeClient("{\"status\":1}",null))
        {
            string response=Log.UploadString(client,"https://fixture.invalid/auth?token=UPLOAD_QUERY_SECRET","POST","original=request");
            Assert(response=="{\"status\":1}","Response changed.");
            Assert(client.Request.Method=="POST"&&Encoding.UTF8.GetString(client.Request.Upload.ToArray())=="original=request","Original request changed.");
        }
        Assert(!Content().Contains("UPLOAD_QUERY_SECRET"),"Upload URL leaked query.");
    }
    private static void UploadFailure()
    {
        FakeResponse response=new FakeResponse("{\"status\":9,\"errorCode\":\"DENIED\",\"deviceId\":\"ERROR_DEVICE_SECRET\"}",true);
        WebException expected=new WebException("backend token=EXCEPTION_TOKEN_SECRET",null,WebExceptionStatus.ProtocolError,response);
        using(FakeClient client=new FakeClient(null,expected))
        {
            Exception actual=null;try{Log.UploadString(client,"https://fixture.invalid/auth","body");}catch(Exception e){actual=e;}
            Assert(Object.ReferenceEquals(actual,expected),"Wrapper replaced the original exception.");
            // WebClient itself resets the fake GetResponse frame in UploadDataInternal.
            // The wrapper must retain the WebClient frames it actually receives.
            Assert(actual.StackTrace.Contains("System.Net.WebClient.UploadString"),"Original WebClient throwing frames were lost.");
            Assert(!actual.StackTrace.TrimStart().StartsWith("at ProxyDiagnostics.Log.UploadString",StringComparison.Ordinal),"Wrapper reset the exception stack.");
            Assert(response.Stream.Position==0,"Seekable response stream position changed.");
        }
        Assert(!Content().Contains("ERROR_DEVICE_SECRET")&&!Content().Contains("EXCEPTION_TOKEN_SECRET"),"Error logging leaked a secret.");
        Assert(Content().Contains("DENIED")&&Content().Contains("ProtocolError"),"Error status/body missing.");
    }
    private static void NonSeekable()
    {
        FakeResponse response=new FakeResponse("{\"status\":7,\"token\":\"ERROR_STREAM_SECRET\"}",false);
        WebException error=new WebException("failure",null,WebExceptionStatus.ProtocolError,response);
        Log.ObserveException("preserve",error);Assert(response.Stream.BytesRead==0,"Default logging consumed a non-seekable body.");
        Environment.SetEnvironmentVariable("HISUITE_PROXY_CAPTURE_ERROR_BODY","1");
        Log.ObserveException("capture",error);
        Environment.SetEnvironmentVariable("HISUITE_PROXY_CAPTURE_ERROR_BODY",null);
        Assert(response.Stream.BytesRead>0,"Explicit capture did not read the body.");
        Assert(Content().Contains("consumed_error_body")&&!Content().Contains("ERROR_STREAM_SECRET"),"Opt-in capture was not annotated/redacted.");
    }
    private static void CaughtObject(){Log.Caught(new DangerousObject(),"caught");Assert(!Content().Contains("OBJECT_SECRET"),"Caught object value leaked.");}
    private static void Concurrency()
    {
        Parallel.For(0,150,i=>Log.Event("parallel","{\"status\":"+i+"}"));
        Log.ObserveResponse("oversized",null,"{\"message\":\""+new string('中',60000)+"\"}");
        Log.ObserveRequest("oversized-json",null,"{\"message\":\""+new string('x',70000)+"\"}");
        JObject[] rows=Records();long prior=0;
        foreach(string line in Content().Split(new[]{"\r\n","\n"},StringSplitOptions.RemoveEmptyEntries))Assert(Encoding.UTF8.GetByteCount(line)+2<=65536,"Record exceeded 64 KiB.");
        foreach(JObject row in rows){long seq=(long)row["sequence"];Assert(seq>prior,"Sequence order/uniqueness failed.");prior=seq;}
        Assert(rows.Count(row=>(string)row["event"]=="parallel")==150,"Concurrent records were lost.");
    }

    public sealed class Display { public object e; public Form1 form; }
    public sealed class Form1
    {
        public bool newMethod=true,lastrequestIsMine=false,selectedRadioChecked=true;
        public object targetRomData=new Dictionary<string,object>{{"version","9.1.0.345"},{"deviceId","TARGET_DEVICE_SECRET"}};
        public Control textBox1=new Control("https://cdn.invalid/package/?token=SELECTION_SECRET",false);
        public Control textBox2=new Control("BLA-LGRP2-OVS 9.1.0.345",false);
        public Control textBox3=new Control("IGNORED_CONTROL_SECRET",false);
        public Control radioButton1=new Control("",true);
        public Control textBox8=new Control("1",false);
    }
    public sealed class Control { public string Text{get;private set;} public bool Checked{get;private set;} public Control(string text,bool check){Text=text;Checked=check;} }
    public sealed class FakeHttpClient{public FakeMessage Request{get;}=new FakeMessage(false);public FakeMessage Response{get;}=new FakeMessage(true);}
    public sealed class FakeMessage
    {
        public string Url{get;}="https://fixture.invalid/full/";public string Method{get;}="GET";public bool HasBody{get;}=false;
        public int StatusCode{get;}=206;public long ContentLength{get;}=16;public FakeHeaders Headers{get;}
        public FakeMessage(bool response){Headers=new FakeHeaders(response);}
    }
    public sealed class FakeHeaders
    {
        public Dictionary<string,FakeHeader> Headers{get;}
        public FakeHeaders(bool response){Headers=new Dictionary<string,FakeHeader>{{response?"Content-Range":"Range",new FakeHeader(response?"bytes 0-15/32768":"bytes=0-15")},{"Authorization",new FakeHeader("FORBIDDEN_HEADER_SECRET")}};}
    }
    public sealed class FakeHeader{public string Value{get;}public FakeHeader(string value){Value=value;}}
    private sealed class DangerousObject { public override string ToString(){throw new Exception("OBJECT_SECRET");} }
    private sealed class FakeClient:WebClient
    {
        private static FakeRequest active;
        internal readonly FakeRequest Request;
        static FakeClient(){WebRequest.RegisterPrefix("https://fixture.invalid/",new Creator());}
        internal FakeClient(string result,WebException error){Encoding=Encoding.UTF8;Request=new FakeRequest(result,error);active=Request;}
        protected override WebRequest GetWebRequest(Uri address){if(address.Host!="fixture.invalid")throw new InvalidOperationException("Tests prohibit real network requests.");return base.GetWebRequest(address);}
        private sealed class Creator:IWebRequestCreate{public WebRequest Create(Uri uri){active.Address=uri;return active;}}
    }
    private sealed class FakeRequest:WebRequest
    {
        internal readonly MemoryStream Upload=new MemoryStream(); private readonly string result; private readonly WebException error;
        internal Uri Address;
        internal FakeRequest(string result,WebException error){this.result=result;this.error=error;}
        public override string Method{get;set;} public override long ContentLength{get;set;} public override WebHeaderCollection Headers{get;set;}=new WebHeaderCollection();
        public override Uri RequestUri{get{return Address;}}
        public override Stream GetRequestStream(){return Upload;}
        public override WebResponse GetResponse(){if(error!=null)throw error;return new FakeResponse(result,true);}
        public override void Abort(){}
    }
    private sealed class FakeResponse:WebResponse
    {
        internal readonly BodyStream Stream;
        internal FakeResponse(string body,bool seek){Stream=new BodyStream(Encoding.UTF8.GetBytes(body??""),seek);}
        public override WebHeaderCollection Headers{get{return new WebHeaderCollection{{"Content-Type","application/json; charset=utf-8"}};}}
        public override long ContentLength{get{return Stream.Length;}}
        public override Stream GetResponseStream(){return Stream;}
    }
    private sealed class BodyStream:Stream
    {
        private readonly MemoryStream inner;private readonly bool seek;internal int BytesRead;
        internal BodyStream(byte[] bytes,bool seek){inner=new MemoryStream(bytes);this.seek=seek;}
        public override bool CanRead{get{return true;}}public override bool CanSeek{get{return seek;}}public override bool CanWrite{get{return false;}}
        public override long Length{get{return inner.Length;}}public override long Position{get{return inner.Position;}set{inner.Position=value;}}
        public override int Read(byte[] buffer,int offset,int count){int n=inner.Read(buffer,offset,count);BytesRead+=n;return n;}
        public override long Seek(long offset,SeekOrigin origin){return inner.Seek(offset,origin);}public override void Flush(){}
        public override void SetLength(long value){throw new NotSupportedException();}public override void Write(byte[] buffer,int offset,int count){throw new NotSupportedException();}
    }
}
