#include "record.h"
using namespace cgss;

static const char* shader = R"(
Texture2D uiFrame:register(t0); Texture2D art:register(t1); Texture2D introArt:register(t2);
Texture2D glassArt:register(t3);Texture2D glassIntro:register(t4);
SamplerState filtering:register(s0);
cbuffer Settings:register(b0){float4 appearance;float4 geometry;float4 animation;float4 avatar;float4 material;};
struct V{float4 position:SV_POSITION;float2 uv:TEXCOORD0;};
V VS(uint id:SV_VertexID){V v;v.uv=float2((id<<1)&2,id&2);v.position=float4(v.uv*float2(2,-2)+float2(-1,1),0,1);return v;}
float3 procedural(float2 uv){
 float t=animation.x*.08;float band=sin(uv.x*4+t)*.13+sin(uv.x*7-t*.8)*.06;
 float glow=exp(-pow((uv.y-.50-band)*4.0,2));
 float3 low=float3(.025,.055,.10);float3 high=float3(.10,.64,.50);
 if(animation.w>0.5){low=float3(.065,.035,.10);high=float3(.55,.25,.65);}
 if(animation.w>1.5){low=float3(.045,.065,.11);high=float3(.20,.39,.75);}
 return low+high*glow*(.35+.4*uv.x);
}
float3 readArt(Texture2D source,float2 q){
 float4 a=source.SampleLevel(filtering,q,0);return lerp(float3(.03,.04,.055),a.rgb,a.a);
}
float4 Frost(V v):SV_TARGET{
 uint sw,sh;art.GetDimensions(sw,sh);
 float coverScale=max(geometry.x/float(sw),geometry.y/float(sh));
 float radius=material.y*min(geometry.x,geometry.y)/1080;
 float2 blurStep=radius/(float2(sw,sh)*coverScale*3);
 float3 color=0;float total=0;
 [unroll]for(int y=-3;y<=3;y++)[unroll]for(int x=-3;x<=3;x++){
  float weight=exp(-float(x*x+y*y)/6);
  color+=readArt(art,saturate(v.uv+float2(x,y)*blurStep))*weight;total+=weight;
 }
 float luminance=dot(color/total,float3(.2126,.7152,.0722));
 return float4(luminance,luminance,luminance,1);
}
float2 artworkUv(float2 uv,float2 size){
 float2 q=uv;float outputRatio=geometry.x/geometry.y;float mediaRatio=size.x/size.y;
 // Cover is opt-in; contain modes leave every original pixel inside the sharp rectangle.
 if(geometry.z>3.5){
  if(outputRatio>mediaRatio)q.y=(q.y-.5)*mediaRatio/outputRatio+.5;else q.x=(q.x-.5)*outputRatio/mediaRatio+.5;
 }else if(geometry.z>0.5){
  if(outputRatio>mediaRatio)q.x=(q.x-.5)*outputRatio/mediaRatio+.5;else q.y=(q.y-.5)*mediaRatio/outputRatio+.5;}
 return q;
}
float3 fittedArt(Texture2D source,Texture2D frost,float2 uv,float2 size){
 float2 q=artworkUv(uv,size);float outputRatio=geometry.x/geometry.y;float mediaRatio=size.x/size.y;
 float3 result=readArt(source,saturate(q));
 if(any(q<0)||any(q>1)){
  if(geometry.z<1.5)result=float3(.025,.025,.025);
  else{
   float2 backdropUv=uv;
   if(outputRatio>mediaRatio)backdropUv.y=(uv.y-.5)*mediaRatio/outputRatio+.5;
   else backdropUv.x=(uv.x-.5)*outputRatio/mediaRatio+.5;
   // Neutral tinted glass: no color spill, edge stretching, or bright glow around the original.
   float base=geometry.z>2.5?.975:.025;
   float value=lerp(frost.SampleLevel(filtering,backdropUv,0).r,base,material.x);
   float grain=frac(sin(dot(floor(uv*geometry.xy),float2(12.9898,78.233)))*43758.5453)-.5;
   result=saturate(value+grain*.004).xxx;
  }
 }
 return result;
}
float hash21(float2 p){return frac(sin(dot(p,float2(127.1,311.7)))*43758.5453);}
float mistNoise(float2 p){float2 i=floor(p),f=frac(p);f=f*f*(3-2*f);return lerp(lerp(hash21(i),hash21(i+float2(1,0)),f.x),lerp(hash21(i+float2(0,1)),hash21(i+1),f.x),f.y);}
float3 manor(float2 uv){
 float3 color=fittedArt(art,glassArt,uv,animation.yz);float2 q=artworkUv(uv,animation.yz);
 if(any(q<0)||any(q>1))return color;
 float t=animation.x;
 // Only warm practical lights flicker; the composition and source pixels never pan or zoom.
 float warm=saturate((color.r-color.b)*2)*smoothstep(.22,.7,color.r);
 float flicker=sin(t*2.3)*.55+sin(t*4.9+.7)*.3+sin(t*8.2)*.15;
 color+=float3(.032,.014,.003)*warm*flicker;
 float valley=smoothstep(.44,.72,q.x)*smoothstep(.3,.5,q.y)*(1-smoothstep(.75,.94,q.y));
 float fog=mistNoise(q*float2(7,12)+float2(t*.012,-t*.004))*.65+mistNoise(q*float2(17,24)-float2(t*.007,0))*.35;
 color+=float3(.018,.022,.030)*(fog-.45)*valley;
 [unroll]for(int i=0;i<8;i++){
  float seed=hash21(float2(i,7));float phase=frac(t*(.012+seed*.008)+seed);
  float2 lightPosition=float2(.035+hash21(float2(i,19))*.48+sin(t*.24+i)*.009,.98-phase*.43);
  float2 d=(q-lightPosition)*float2(animation.y/animation.z,1);
  float mote=exp(-dot(d,d)*190000)*sin(phase*3.14159)*(.5+.5*sin(t*1.4+i));
  color+=float3(.18,.082,.015)*mote;
 }
 return saturate(color);
}
float3 sampleArt(float2 uv){
 float3 result=0;
 if(material.z>.5)result=manor(uv);
 else if(appearance.w>0.5)result=procedural(uv);
 else result=fittedArt(art,glassArt,uv,animation.yz);
 return result;
}
float themeLight(){
 // Sample the conversation area. A persistent navigation rail can have the opposite palette.
 float votes=0;
 votes+=step(.60,dot(uiFrame.SampleLevel(filtering,float2(.52,.18),0).rgb,float3(.2126,.7152,.0722)));
 votes+=step(.60,dot(uiFrame.SampleLevel(filtering,float2(.76,.44),0).rgb,float3(.2126,.7152,.0722)));
 votes+=step(.60,dot(uiFrame.SampleLevel(filtering,float2(.92,.68),0).rgb,float3(.2126,.7152,.0722)));
 votes+=step(.60,dot(uiFrame.SampleLevel(filtering,float2(.55,.06),0).rgb,float3(.2126,.7152,.0722)));
 votes+=step(.60,dot(uiFrame.SampleLevel(filtering,float2(.85,.93),0).rgb,float3(.2126,.7152,.0722)));
 return step(2.5,votes);
}
float4 PS(V v):SV_TARGET{
 float4 ui=uiFrame.Sample(filtering,v.uv);float3 picture=sampleArt(v.uv);
 uint uw,uh;uiFrame.GetDimensions(uw,uh);float2 sourceSize=float2(uw,uh);
 float high=max(ui.r,max(ui.g,ui.b)),low=min(ui.r,min(ui.g,ui.b));
 float light=themeLight();
 if(avatar.w>.5&&v.uv.x*sourceSize.x<avatar.x*2){
  float x=avatar.x/sourceSize.x;float railVotes=0;
  railVotes+=step(.60,dot(uiFrame.SampleLevel(filtering,float2(x,.18),0).rgb,float3(.2126,.7152,.0722)));
  railVotes+=step(.60,dot(uiFrame.SampleLevel(filtering,float2(x,.44),0).rgb,float3(.2126,.7152,.0722)));
  railVotes+=step(.60,dot(uiFrame.SampleLevel(filtering,float2(x,.68),0).rgb,float3(.2126,.7152,.0722)));
  light=step(1.5,railVotes);
 }
 float neutral=1-smoothstep(.035,.08,high-low);
 float base=light>.5?low:high;
 [unroll]for(int y=-1;y<=1;y++)[unroll]for(int x=-1;x<=1;x++){
  float3 n=uiFrame.Sample(filtering,v.uv+float2(x,y)*3/sourceSize).rgb;
  float level=light>.5?min(n.r,min(n.g,n.b)):max(n.r,max(n.g,n.b));
  base=light>.5?max(base,level):min(base,level);
 }
 float eligible=lerp(1-smoothstep(.24,.34,base),smoothstep(.62,.80,base),light);
 float ink=lerp(saturate((high-base)/max(.01,1-base)),saturate((base-low)/max(.01,base)),light);
 float panel=lerp(smoothstep(.13,.25,base),1-smoothstep(.78,.90,base),light);
 float3 tint=lerp(float3(.035,.045,.06),float3(.94,.95,.96),light);
 float3 background=lerp(picture,tint,appearance.x);
 background=lerp(background,tint,panel*appearance.y);
 // Preserve the original foreground coverage; do not redraw or thicken glyphs or clamp the artwork.
 float3 composed=saturate(ui.rgb+(background-base)*(1-ink)*neutral*eligible);
 if(appearance.z>.5)composed=picture;
 if(appearance.z<-.5)composed=ui.rgb;
 // Preserve the actual avatar pixels, in source-frame coordinates (also correct in scaled previews).
 if(avatar.w>.5){float keep=1-smoothstep(avatar.z-.5,avatar.z+.5,length(v.uv*sourceSize-avatar.xy));composed=lerp(composed,ui.rgb,keep);}
 float intro=geometry.w;
 if(intro>0){
  float2 p=(v.uv-.5)*float2(geometry.x/geometry.y,1);float r=length(p);
  float ring=exp(-pow((r-(.06+.35*(1-intro)))*90,2));
  float3 opening=picture*.30+float3(.07,.78,.62)*ring*.8;
  if(animation.w<-0.5){
   uint iw,ih;introArt.GetDimensions(iw,ih);
   opening=fittedArt(introArt,glassIntro,v.uv,float2(iw,ih));
  }
  composed=lerp(composed,opening,smoothstep(0,.2,intro));
 }
 return float4(composed,1);
})";

LRESULT CALLBACK WindowProc(HWND hwnd,UINT message,WPARAM wp,LPARAM lp){
 if(message==WM_NCHITTEST)return HTTRANSPARENT;
 if(message==WM_MOUSEACTIVATE)return MA_NOACTIVATE;
 if(message==WM_ERASEBKGND)return 1;
 if(message==WM_DESTROY){PostQuitMessage(0);return 0;}
 return DefWindowProcW(hwnd,message,wp,lp);
}

class LayoutWatch{
 HWND target,canvas;HWINEVENTHOOK moveHook=nullptr,locationHook=nullptr;
 bool dirty=false,moving=false;ULONGLONG changed=0;
 inline static thread_local LayoutWatch* current=nullptr;
 static void CALLBACK Event(HWINEVENTHOOK,DWORD event,HWND window,LONG object,LONG,DWORD,DWORD){
  auto self=current;if(!self||window!=self->target)return;
  if(event==EVENT_OBJECT_LOCATIONCHANGE&&object!=OBJID_WINDOW)return;
  if(event==EVENT_SYSTEM_MOVESIZESTART)self->moving=true;
  if(event==EVENT_SYSTEM_MOVESIZEEND)self->moving=false;
  self->Touch();
 }
public:
 LayoutWatch(HWND window,HWND overlay,DWORD pid):target(window),canvas(overlay){
  current=this;
  moveHook=SetWinEventHook(EVENT_SYSTEM_MOVESIZESTART,EVENT_SYSTEM_MOVESIZEEND,nullptr,Event,pid,0,WINEVENT_OUTOFCONTEXT|WINEVENT_SKIPOWNPROCESS);
  locationHook=SetWinEventHook(EVENT_OBJECT_LOCATIONCHANGE,EVENT_OBJECT_LOCATIONCHANGE,nullptr,Event,pid,0,WINEVENT_OUTOFCONTEXT|WINEVENT_SKIPOWNPROCESS);
 }
 ~LayoutWatch(){if(moveHook)UnhookWinEvent(moveHook);if(locationHook)UnhookWinEvent(locationHook);current=nullptr;}
 void Touch(){dirty=true;changed=GetTickCount64();ShowWindow(canvas,SW_HIDE);}
 bool Consume(){bool result=dirty;dirty=false;return result;}
 bool Settling()const{return moving||(changed&&GetTickCount64()-changed<120);}
};

class Renderer{
 Config const& c; HWND canvas=nullptr;
 com_ptr<ID3D11Device> device;com_ptr<ID3D11DeviceContext> context;
 com_ptr<IDXGISwapChain1> swap;com_ptr<IDCompositionDevice> comp;com_ptr<IDCompositionTarget> compTarget;com_ptr<IDCompositionVisual> visual;
 com_ptr<ID3D11RenderTargetView> target;com_ptr<ID3D11VertexShader> vs;com_ptr<ID3D11PixelShader> ps;
 com_ptr<ID3D11SamplerState> sampler;com_ptr<ID3D11Buffer> constants;
 com_ptr<ID3D11PixelShader> frostPs;
 com_ptr<ID3D11Texture2D> frostTexture[2];com_ptr<ID3D11RenderTargetView> frostTarget[2];com_ptr<ID3D11ShaderResourceView> frostView[2];
 std::unique_ptr<Capture> clientCapture;std::unique_ptr<Media> media,intro;
 std::unique_ptr<LayoutWatch> layout;
 int width=0,height=0;bool saved=false;unsigned long long frames=0;Clock::time_point started=Clock::now();
 RECT observedBounds{};UINT observedDpi=0;Clock::time_point mismatchSince{},lastRecovery{};int recoveries=0;
public:
 explicit Renderer(Config const& config):c(config){
  phase="device";
  HRESULT created=D3D11CreateDevice(nullptr,D3D_DRIVER_TYPE_HARDWARE,nullptr,D3D11_CREATE_DEVICE_BGRA_SUPPORT|D3D11_CREATE_DEVICE_VIDEO_SUPPORT,nullptr,0,D3D11_SDK_VERSION,device.put(),nullptr,context.put());
  if(FAILED(created))check_hresult(D3D11CreateDevice(nullptr,D3D_DRIVER_TYPE_HARDWARE,nullptr,D3D11_CREATE_DEVICE_BGRA_SUPPORT,nullptr,0,D3D11_SDK_VERSION,device.put(),nullptr,context.put()));
  auto multithread=device.as<ID3D10Multithread>();multithread->SetMultithreadProtected(TRUE);
  phase="media";
  media=std::make_unique<Media>(device.get(),context.get(),c);
  if(c.mode==L"probe"||c.mode==L"record")return;
  if(c.target){VerifyWindow(c.target,c.targetPid);clientCapture=std::make_unique<Capture>(device.get(),c.target);}
  if(c.mode==L"preview"){
   if(!IsWindow(c.parentWindow))Fail("preview_closed");RECT r{};GetClientRect(c.parentWindow,&r);width=std::max(1L,r.right);height=std::max(1L,r.bottom);
  }else{width=clientCapture->width;height=clientCapture->height;}
  if(!c.introPath.empty()&&c.introSeconds>0){Config i=c;i.kind=L"video";i.path=c.introPath;intro=std::make_unique<Media>(device.get(),context.get(),i);}
  CreateCanvas();CreateShaders();
  if(c.target)layout=std::make_unique<LayoutWatch>(c.target,canvas,c.targetPid);
 }
 ~Renderer(){layout.reset();if(canvas)DestroyWindow(canvas);}
 void Run(Watchdog& watchdog){
  std::unique_ptr<Lifetime> parent,client;
  if(c.parentPid)parent=std::make_unique<Lifetime>(c.parentPid,c.parentBirth);
  if(c.targetPid)client=std::make_unique<Lifetime>(c.targetPid,c.targetBirth,true);
  Handle stop(c.stopEvent.empty()?nullptr:OpenEventW(SYNCHRONIZE,FALSE,c.stopEvent.c_str()));
  if(!c.stopEvent.empty()&&!stop.value)Fail("stop_channel_missing");
  auto lastReport=Clock::now();auto hiddenAt=Clock::time_point{};bool quit=false;
  if(c.mode==L"record"){
   while(!media->view){watchdog.Pulse();media->Update();if(Seconds(started)>12)Fail("scene_no_frames");if(!parent->Alive()||WaitForSingleObject(stop.value,0)==WAIT_OBJECT_0)Fail("record_cancelled");Sleep(10);}
   VideoRecorder recorder(device.get(),media->Texture(),c.recordPath,media->width,media->height);
   int sourceW=media->width,sourceH=media->height;auto began=Clock::now();long long index=0;
   while(Seconds(began)<c.recordSeconds){
    watchdog.Pulse();if(!parent->Alive()||WaitForSingleObject(stop.value,0)==WAIT_OBJECT_0)Fail("record_cancelled");
    media->Update();if(media->width!=sourceW||media->height!=sourceH)Fail("record_resize");
    double elapsed=index==0?0:Seconds(began);if(elapsed>=c.recordSeconds)break;
    recorder.Write(context.get(),media->Texture(),static_cast<LONGLONG>(elapsed*10000000),static_cast<LONGLONG>(std::min(1./30,c.recordSeconds-elapsed)*10000000));
    index++;
    if(index==3||Seconds(lastReport)>.2){Status(c,L"recording",recorder.width,recorder.height,sourceW,sourceH,index,0,"",media->decoded,elapsed);lastReport=Clock::now();}
    auto due=began+std::chrono::microseconds(index*1000000/30);if(due>Clock::now())std::this_thread::sleep_until(due);
   }
   watchdog.Pulse();recorder.Finish();Status(c,L"completed",recorder.width,recorder.height,sourceW,sourceH,index,0,"",media->decoded,c.recordSeconds);return;
  }
  if(c.mode==L"probe"){
   while(!media->view){media->Update();if(Seconds(started)>12)Fail("scene_no_frames");if(parent&&!parent->Alive())Fail("owner_closed");Sleep(15);}
   if(c.kind==L"window"&&!c.snapshot.empty()){
    auto sampled=Clock::now();bool early=false;
    while(Seconds(sampled)<2){
     watchdog.Pulse();if(parent&&!parent->Alive())Fail("owner_closed");
     if(stop.value&&WaitForSingleObject(stop.value,0)==WAIT_OBJECT_0)Fail("probe_cancelled");
     media->Update();
     if(!early&&Seconds(sampled)>1&&media->view){SavePng(device.get(),context.get(),media->Texture(),(std::filesystem::path(c.snapshot).parent_path()/L"earlier.png").wstring(),media->width,media->height);early=true;}
     Sleep(16);
    }
   }
   if(!c.snapshot.empty())SavePng(device.get(),context.get(),media->Texture(),c.snapshot,media->width,media->height);
   Status(c,L"ready",media->width,media->height,media->width,media->height,media->decoded,0,"",media->decoded,media->duration);return;
  }
  while(!quit&&(!parent||parent->Alive())&&(!client||client->Alive())){
   watchdog.Pulse();
   if(stop.value&&WaitForSingleObject(stop.value,0)==WAIT_OBJECT_0)break;
   if(c.maxSeconds>0&&Seconds(started)>c.maxSeconds)break;
   MSG msg{};while(PeekMessageW(&msg,nullptr,0,0,PM_REMOVE)){if(msg.message==WM_QUIT)quit=true;TranslateMessage(&msg);DispatchMessageW(&msg);}
   if(c.target){
    if(!IsWindow(c.target))break;
    DWORD cloaked=0;DwmGetWindowAttribute(c.target,DWMWA_CLOAKED,&cloaked,sizeof(cloaked));
    if(IsIconic(c.target)||IsHungAppWindow(c.target)||cloaked){ShowWindow(canvas,SW_HIDE);clientCapture->Invalidate();media->Suspend();Sleep(50);continue;}
    if(!IsWindowVisible(c.target)){
     ShowWindow(canvas,SW_HIDE);clientCapture->Invalidate();media->Suspend();if(hiddenAt==Clock::time_point{})hiddenAt=Clock::now();
     if(Seconds(hiddenAt)>3)break;Sleep(50);continue;
    }hiddenAt=Clock::time_point{};
    RECT bounds{};if(FAILED(DwmGetWindowAttribute(c.target,DWMWA_EXTENDED_FRAME_BOUNDS,&bounds,sizeof(bounds))))GetWindowRect(c.target,&bounds);
    UINT dpi=GetDpiForWindow(c.target);
    bool changed=(bounds.right-bounds.left)!=(observedBounds.right-observedBounds.left)||(bounds.bottom-bounds.top)!=(observedBounds.bottom-observedBounds.top)||dpi!=observedDpi;
    bool event=layout->Consume();
    if(changed){layout->Touch();layout->Consume();}
    if(changed||event){clientCapture->Invalidate();ShowWindow(canvas,SW_HIDE);mismatchSince={};lastRecovery={};recoveries=0;}
    observedBounds=bounds;observedDpi=dpi;
    if(layout->Settling()){
     media->Suspend();if(Seconds(lastReport)>.25){Status(c,L"resizing",bounds.right-bounds.left,bounds.bottom-bounds.top,media->width,media->height,frames);lastReport=Clock::now();}
     Sleep(16);continue;
    }
   }
   if(c.mode==L"preview"&&!IsWindow(c.parentWindow))break;
   if(c.mode==L"preview"&&(!IsWindowVisible(GetAncestor(c.parentWindow,GA_ROOT))||IsIconic(GetAncestor(c.parentWindow,GA_ROOT)))){ShowWindow(canvas,SW_HIDE);media->Suspend();Sleep(60);continue;}
   media->Update();if(intro&&Seconds(started)<c.introSeconds)intro->Update();
   if(clientCapture)clientCapture->Update(device.get(),context.get());
   if(clientCapture&&(!clientCapture->view||clientCapture->width!=(observedBounds.right-observedBounds.left)||clientCapture->height!=(observedBounds.bottom-observedBounds.top))){
    ShowWindow(canvas,SW_HIDE);media->Suspend();
    if(mismatchSince==Clock::time_point{})mismatchSince=Clock::now();
    if(Seconds(mismatchSince)>1&&recoveries<3&&(lastRecovery==Clock::time_point{}||Seconds(lastRecovery)>1)){
     clientCapture.reset();clientCapture=std::make_unique<Capture>(device.get(),c.target);lastRecovery=Clock::now();recoveries++;
    }
    if(Seconds(mismatchSince)>6)Fail("capture_resize_timeout");
    if(Seconds(lastReport)>.25){Status(c,L"resizing",observedBounds.right-observedBounds.left,observedBounds.bottom-observedBounds.top,media->width,media->height,frames);lastReport=Clock::now();}
    Sleep(10);continue;
   }
   mismatchSince={};recoveries=0;
   if(!media->view||(clientCapture&&!clientCapture->view)){ShowWindow(canvas,SW_HIDE);if(Seconds(started)>12&&frames==0)Fail("capture_no_frames");Sleep(10);continue;}
   int desiredW=width,desiredH=height;
   if(c.mode==L"preview"){RECT r{};GetClientRect(c.parentWindow,&r);desiredW=std::max(1L,r.right);desiredH=std::max(1L,r.bottom);}
   else{desiredW=clientCapture->width;desiredH=clientCapture->height;}
   if(desiredW!=width||desiredH!=height){ShowWindow(canvas,SW_HIDE);width=desiredW;height=desiredH;Resize(true);}
   Draw();if(!Position()){Sleep(10);continue;}frames++;
   if(frames==3||Seconds(lastReport)>1){Report();lastReport=Clock::now();}
   Sleep(16);
  }
  ShowWindow(canvas,SW_HIDE);Status(c,L"stopped",width,height,media->width,media->height,frames,0,"",media->decoded,media->duration);
 }
private:
 void CreateCanvas(){
  phase="canvas";
  WNDCLASSW cls{};cls.lpfnWndProc=WindowProc;cls.hInstance=GetModuleHandleW(nullptr);cls.lpszClassName=L"CGSS.NativeCanvas";RegisterClassW(&cls);
  DWORD ex=WS_EX_NOACTIVATE|WS_EX_LAYERED|WS_EX_TRANSPARENT;
  DWORD style=WS_POPUP;
  ex|=WS_EX_TOOLWINDOW;
  canvas=CreateWindowExW(ex,cls.lpszClassName,L"ChatGPT Skin Studio — background",style,0,0,width,height,
    nullptr,nullptr,cls.hInstance,nullptr);
  check_bool(canvas!=nullptr);phase="layered";check_bool(SetLayeredWindowAttributes(canvas,0,255,LWA_ALPHA));
  auto dxgi=device.as<IDXGIDevice>();com_ptr<IDXGIAdapter> adapter;check_hresult(dxgi->GetAdapter(adapter.put()));
  com_ptr<IDXGIFactory2> factory;check_hresult(adapter->GetParent(__uuidof(IDXGIFactory2),factory.put_void()));
  DXGI_SWAP_CHAIN_DESC1 desc{};desc.Width=width;desc.Height=height;desc.Format=DXGI_FORMAT_B8G8R8A8_UNORM;desc.SampleDesc.Count=1;
  desc.BufferUsage=DXGI_USAGE_RENDER_TARGET_OUTPUT;desc.BufferCount=2;desc.SwapEffect=DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL;desc.AlphaMode=DXGI_ALPHA_MODE_PREMULTIPLIED;
  phase="swap";check_hresult(factory->CreateSwapChainForComposition(device.get(),&desc,nullptr,swap.put()));
  phase="composition";
  check_hresult(DCompositionCreateDevice(dxgi.get(),__uuidof(IDCompositionDevice),comp.put_void()));
  check_hresult(comp->CreateTargetForHwnd(canvas,TRUE,compTarget.put()));check_hresult(comp->CreateVisual(visual.put()));
  check_hresult(visual->SetContent(swap.get()));check_hresult(compTarget->SetRoot(visual.get()));check_hresult(comp->Commit());Resize(false);
 }
 void CreateShaders(){
  phase="shader";
  com_ptr<ID3DBlob> v,p,errors;check_hresult(D3DCompile(shader,strlen(shader),nullptr,nullptr,nullptr,"VS","vs_4_0",0,0,v.put(),errors.put()));errors=nullptr;
  HRESULT hr=D3DCompile(shader,strlen(shader),nullptr,nullptr,nullptr,"PS","ps_4_0",0,0,p.put(),errors.put());
  if(FAILED(hr)){if(errors)std::cerr.write(static_cast<char*>(errors->GetBufferPointer()),errors->GetBufferSize());check_hresult(hr);}
  check_hresult(device->CreateVertexShader(v->GetBufferPointer(),v->GetBufferSize(),nullptr,vs.put()));check_hresult(device->CreatePixelShader(p->GetBufferPointer(),p->GetBufferSize(),nullptr,ps.put()));
  com_ptr<ID3DBlob> frostCode;errors=nullptr;
  HRESULT frostResult=D3DCompile(shader,strlen(shader),nullptr,nullptr,nullptr,"Frost","ps_4_0",0,0,frostCode.put(),errors.put());
  if(FAILED(frostResult)){if(errors)std::cerr.write(static_cast<char*>(errors->GetBufferPointer()),errors->GetBufferSize());check_hresult(frostResult);}
  check_hresult(device->CreatePixelShader(frostCode->GetBufferPointer(),frostCode->GetBufferSize(),nullptr,frostPs.put()));
  for(int i=0;i<2;i++){
   D3D11_TEXTURE2D_DESC desc{};desc.Width=128;desc.Height=128;desc.MipLevels=1;desc.ArraySize=1;desc.Format=DXGI_FORMAT_B8G8R8A8_UNORM;desc.SampleDesc.Count=1;desc.BindFlags=D3D11_BIND_RENDER_TARGET|D3D11_BIND_SHADER_RESOURCE;
   check_hresult(device->CreateTexture2D(&desc,nullptr,frostTexture[i].put()));
   check_hresult(device->CreateRenderTargetView(frostTexture[i].get(),nullptr,frostTarget[i].put()));
   check_hresult(device->CreateShaderResourceView(frostTexture[i].get(),nullptr,frostView[i].put()));
  }
  D3D11_SAMPLER_DESC s{};s.Filter=D3D11_FILTER_MIN_MAG_MIP_LINEAR;s.AddressU=s.AddressV=s.AddressW=D3D11_TEXTURE_ADDRESS_CLAMP;s.MaxLOD=D3D11_FLOAT32_MAX;
  check_hresult(device->CreateSamplerState(&s,sampler.put()));
  D3D11_BUFFER_DESC b{};b.ByteWidth=80;b.Usage=D3D11_USAGE_DYNAMIC;b.BindFlags=D3D11_BIND_CONSTANT_BUFFER;b.CPUAccessFlags=D3D11_CPU_ACCESS_WRITE;
  check_hresult(device->CreateBuffer(&b,nullptr,constants.put()));
 }
 void Resize(bool change){
  context->OMSetRenderTargets(0,nullptr,nullptr);target=nullptr;
  if(change)check_hresult(swap->ResizeBuffers(2,width,height,DXGI_FORMAT_UNKNOWN,0));
  com_ptr<ID3D11Texture2D> buffer;check_hresult(swap->GetBuffer(0,__uuidof(ID3D11Texture2D),buffer.put_void()));check_hresult(device->CreateRenderTargetView(buffer.get(),nullptr,target.put()));
  if(change)check_hresult(comp->Commit());
 }
 void BuildFrost(int index,ID3D11ShaderResourceView* source){
  ID3D11ShaderResourceView* empty[5]{};context->PSSetShaderResources(0,5,empty);
  ID3D11RenderTargetView* output=frostTarget[index].get();context->OMSetRenderTargets(1,&output,nullptr);
  D3D11_VIEWPORT viewport{0,0,128,128,0,1};context->RSSetViewports(1,&viewport);
  context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);context->VSSetShader(vs.get(),nullptr,0);context->PSSetShader(frostPs.get(),nullptr,0);
  context->PSSetShaderResources(1,1,&source);ID3D11SamplerState* sample=sampler.get();context->PSSetSamplers(0,1,&sample);context->Draw(3,0);
  context->PSSetShaderResources(0,5,empty);context->OMSetRenderTargets(0,nullptr,nullptr);
 }
 void Draw(){
  phase="draw";
  float progress=c.introSeconds>0?std::max(0.f,1.f-static_cast<float>(Seconds(started))/c.introSeconds):0.f;
  float dpiScale=c.target?static_cast<float>(GetDpiForWindow(c.target))/96.f:1.f;
  float fit=c.fit==L"glass-dark"?2.f:c.fit==L"glass-light"?3.f:c.fit==L"contain"?1.f:c.fit==L"cover"?4.f:0.f;
  float data[20]{c.dim,c.surface,c.introOnly?-1.f:clientCapture?0.f:1.f,c.kind==L"builtin"?1.f:0.f,static_cast<float>(width),static_cast<float>(height),fit,progress,
   static_cast<float>(Seconds(started)),static_cast<float>(media->width),static_cast<float>(media->height),intro&&intro->view&&progress>0?-1.f:static_cast<float>(c.preset),
   26*dpiScale,clientCapture?static_cast<float>(clientCapture->height)-24*dpiScale:0,16*dpiScale,clientCapture?1.f:0.f,c.glassOpacity,c.glassBlur,c.kind==L"builtin"&&c.preset==3?1.f:0.f,0};
  D3D11_MAPPED_SUBRESOURCE mapped{};check_hresult(context->Map(constants.get(),0,D3D11_MAP_WRITE_DISCARD,0,&mapped));memcpy(mapped.pData,data,sizeof(data));context->Unmap(constants.get(),0);
  ID3D11Buffer* b=constants.get();context->PSSetConstantBuffers(0,1,&b);
  bool glass=fit==2.f||fit==3.f;
  if(glass&&(c.kind!=L"builtin"||c.preset==3))BuildFrost(0,media->view.get());
  if(glass&&intro&&intro->view&&progress>0)BuildFrost(1,intro->view.get());
  D3D11_VIEWPORT viewport{0,0,static_cast<float>(width),static_cast<float>(height),0,1};context->RSSetViewports(1,&viewport);
  ID3D11RenderTargetView* r=target.get();context->OMSetRenderTargets(1,&r,nullptr);context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
  context->VSSetShader(vs.get(),nullptr,0);context->PSSetShader(ps.get(),nullptr,0);
  ID3D11ShaderResourceView* views[]{clientCapture?clientCapture->view.get():media->view.get(),media->view.get(),intro&&intro->view?intro->view.get():media->view.get(),frostView[0].get(),frostView[1].get()};context->PSSetShaderResources(0,5,views);
  ID3D11SamplerState* sample=sampler.get();context->PSSetSamplers(0,1,&sample);
  context->Draw(3,0);
  if(!saved&&!c.snapshot.empty()&&Seconds(started)>c.introSeconds+.3){
   com_ptr<ID3D11Texture2D> buffer;check_hresult(swap->GetBuffer(0,__uuidof(ID3D11Texture2D),buffer.put_void()));SavePng(device.get(),context.get(),buffer.get(),c.snapshot,width,height);
   if(clientCapture)SavePng(device.get(),context.get(),clientCapture->texture.get(),(std::filesystem::path(c.snapshot).parent_path()/L"source-ui.png").wstring(),clientCapture->width,clientCapture->height);
   saved=true;
  }
  check_hresult(swap->Present(1,0));ID3D11ShaderResourceView* empty[5]{};context->PSSetShaderResources(0,5,empty);
 }
 bool Position(){
  phase="position";
  if(c.mode==L"preview"){
   POINT p{};check_bool(ClientToScreen(c.parentWindow,&p));HWND root=GetAncestor(c.parentWindow,GA_ROOT);
   RECT size{};GetClientRect(c.parentWindow,&size);
   if(size.right!=width||size.bottom!=height){ShowWindow(canvas,SW_HIDE);return false;}
   // WPF reveals the host only after a valid first frame. Preparation must not wait for visibility.
   if(!IsWindowVisible(c.parentWindow)){ShowWindow(canvas,SW_HIDE);return true;}
   HWND previous=GetWindow(root,GW_HWNDPREV);UINT flags=SWP_NOACTIVATE|SWP_SHOWWINDOW;
   if(previous==canvas)flags|=SWP_NOZORDER;else if(!previous)previous=HWND_TOP;
   check_bool(SetWindowPos(canvas,previous,p.x,p.y,width,height,flags|SWP_NOCOPYBITS));return true;
  }
  RECT r{};if(FAILED(DwmGetWindowAttribute(c.target,DWMWA_EXTENDED_FRAME_BOUNDS,&r,sizeof(r))))check_bool(GetWindowRect(c.target,&r));
  if(!EqualRect(&r,&observedBounds)||r.right-r.left!=width||r.bottom-r.top!=height||GetDpiForWindow(c.target)!=observedDpi||IsIconic(c.target)){
   layout->Touch();ShowWindow(canvas,SW_HIDE);return false;
  }
  HWND previous=GetWindow(c.target,GW_HWNDPREV);UINT flags=SWP_NOACTIVATE|SWP_SHOWWINDOW;
  if(previous==canvas)flags|=SWP_NOZORDER;else if(!previous)previous=HWND_TOP;
  check_bool(SetWindowPos(canvas,previous,r.left,r.top,width,height,flags|SWP_NOCOPYBITS));return true;
 }
 void Report(){
  RECT r{};GetWindowRect(canvas,&r);POINT p{(r.left+r.right)/2,(r.top+r.bottom)/2};DWORD pid=0;GetWindowThreadProcessId(WindowFromPoint(p),&pid);
  if(c.mode==L"render"&&pid==GetCurrentProcessId())Fail("input_intercepted");
  bool prepared=(c.mode==L"render"&&pid!=c.targetPid)||(c.mode==L"preview"&&!IsWindowVisible(c.parentWindow));
  Status(c,prepared?L"prepared":L"ready",width,height,media->width,media->height,frames,pid,"",media->decoded,media->duration);
 }
};

int wmain(int argc,wchar_t**argv){
 Config config;bool mf=false;
 try{
  if(argc!=2)Fail("request_required");SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
  init_apartment(apartment_type::multi_threaded);
  config=Config::Read(std::filesystem::path(argv[1]));
  if(config.mode==L"guard")return GuardScene(config);
  check_hresult(MFStartup(MF_VERSION));mf=true;
  {Watchdog watchdog(config);Renderer renderer(config);renderer.Run(watchdog);}
  MFShutdown();return 0;
 }catch(hresult_error const&e){auto code=ReadableError(e);std::cerr<<code<<std::endl;try{if(!config.status.empty())Status(config,L"error",0,0,0,0,0,0,code.c_str());}catch(...){} }
 catch(std::exception const&e){std::cerr<<e.what()<<std::endl;try{if(!config.status.empty())Status(config,L"error",0,0,0,0,0,0,e.what());}catch(...){} }
 if(mf)MFShutdown();return 1;
}
