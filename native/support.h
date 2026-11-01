#pragma once
#define NOMINMAX
#include <windows.h>
#include <appmodel.h>
#include <dwmapi.h>
#include <d3d11.h>
#include <d3d10.h>
#include <d3dcompiler.h>
#include <dxgi1_2.h>
#include <dcomp.h>
#include <wincodec.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mfreadwrite.h>
#include <propvarutil.h>
#include <windows.graphics.capture.interop.h>
#include <windows.graphics.directx.direct3d11.interop.h>
#include <winrt/base.h>
#include <winrt/Windows.Data.Json.h>
#include <winrt/Windows.Foundation.Metadata.h>
#include <winrt/Windows.Graphics.Capture.h>
#include <winrt/Windows.Graphics.DirectX.h>
#include <winrt/Windows.Graphics.DirectX.Direct3D11.h>
#include <algorithm>
#include <atomic>
#include <chrono>
#include <cmath>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <memory>
#include <string>
#include <thread>
#include <vector>

namespace cgss {
using namespace winrt;
using namespace winrt::Windows::Data::Json;
using namespace winrt::Windows::Graphics::Capture;
using namespace winrt::Windows::Graphics::DirectX;
using namespace winrt::Windows::Graphics::DirectX::Direct3D11;
using Clock = std::chrono::steady_clock;
inline const char* phase="initialization";
inline double Seconds(Clock::time_point start) { return std::chrono::duration<double>(Clock::now() - start).count(); }
inline void Fail(const char* code) { throw std::runtime_error(code); }
inline std::wstring String(JsonObject const& j, const wchar_t* key, const wchar_t* fallback = L"") { return std::wstring(j.GetNamedString(key, fallback)); }
inline double Number(JsonObject const& j, const wchar_t* key, double fallback = 0) { auto n = j.GetNamedNumber(key, fallback); if (!std::isfinite(n)) Fail("invalid_number"); return n; }
inline bool IsInside(std::filesystem::path const& path, std::filesystem::path const& boundary) {
    auto p = std::filesystem::absolute(path).lexically_normal().wstring();
    auto b = std::filesystem::absolute(boundary).lexically_normal().wstring() + L"\\";
    return p.size() >= b.size() && _wcsnicmp(p.c_str(), b.c_str(), b.size()) == 0;
}
struct Config {
    std::wstring mode, kind, path, fit, status, snapshot, stopEvent, title, introPath, enginePath, recordPath;
    DWORD parentPid = 0, targetPid = 0, sourcePid = 0;
    unsigned long long parentBirth = 0, targetBirth = 0, sourceBirth = 0;
    HWND target = nullptr, parentWindow = nullptr, sourceWindow = nullptr;
    float dim = .32f, surface = .7f, glassOpacity = .86f, glassBlur = 40.f, introSeconds = 0, maxSeconds = 0;
    int preset = 0;
    double recordSeconds = 20;
    bool introOnly = false;
    static Config Read(std::filesystem::path const& file) {
        if (!file.is_absolute() || !std::filesystem::is_regular_file(file) || std::filesystem::file_size(file) > 65536) Fail("invalid_request");
        std::ifstream input(file, std::ios::binary);
        std::string data((std::istreambuf_iterator<char>(input)), {});
        auto j = JsonObject::Parse(to_hstring(data));
        Config c;
        c.mode = String(j, L"mode", L"render"); c.kind = String(j, L"sourceKind", L"builtin");
        c.path = String(j, L"sourcePath"); c.fit = String(j, L"fit", L"glass-dark");
        if(c.fit==L"extend")c.fit=L"glass-dark";
        c.status = String(j, L"statusPath"); c.snapshot = String(j, L"snapshotPath");
        c.stopEvent = String(j, L"stopEvent"); c.title = String(j, L"sourceTitle"); c.introPath = String(j, L"introPath");
        c.enginePath = String(j,L"enginePath");
        c.recordPath = String(j,L"recordPath");c.recordSeconds=Number(j,L"recordSeconds",20);
        c.introOnly=j.GetNamedBoolean(L"introOnly",false);
        c.parentPid = static_cast<DWORD>(Number(j, L"parentPid")); c.targetPid = static_cast<DWORD>(Number(j, L"targetPid"));
        c.sourcePid = static_cast<DWORD>(Number(j, L"sourcePid"));
        auto birth = [&](const wchar_t* name) { auto v = String(j, name, L"0"); return std::stoull(v); };
        c.parentBirth = birth(L"parentBirth"); c.targetBirth = birth(L"targetBirth"); c.sourceBirth = birth(L"sourceBirth");
        c.target = reinterpret_cast<HWND>(static_cast<uintptr_t>(std::stoull(String(j, L"targetHwnd", L"0"))));
        c.parentWindow = reinterpret_cast<HWND>(static_cast<uintptr_t>(std::stoull(String(j, L"previewHwnd", L"0"))));
        c.sourceWindow = reinterpret_cast<HWND>(static_cast<uintptr_t>(std::stoull(String(j, L"sourceHwnd", L"0"))));
        c.dim = static_cast<float>(Number(j, L"dim", .32)); c.surface = static_cast<float>(Number(j, L"surface", .7));
        c.glassOpacity = static_cast<float>(Number(j, L"glassOpacity", .86)); c.glassBlur = static_cast<float>(Number(j, L"glassBlur", 40));
        c.introSeconds = static_cast<float>(Number(j, L"introSeconds")); c.maxSeconds = static_cast<float>(Number(j, L"maxSeconds"));
        c.preset = static_cast<int>(Number(j, L"preset"));
        if (c.mode != L"render" && c.mode != L"preview" && c.mode != L"probe" && c.mode != L"guard" && c.mode != L"record") Fail("invalid_mode");
        if (c.kind != L"builtin" && c.kind != L"image" && c.kind != L"video" && c.kind != L"window") Fail("invalid_source");
        if (c.fit != L"glass-dark" && c.fit != L"glass-light" && c.fit != L"contain" && c.fit != L"cover" && c.fit != L"stretch") Fail("invalid_fit");
        if (c.status.empty() || !IsInside(c.status, file.parent_path()) || (!c.snapshot.empty() && !IsInside(c.snapshot, file.parent_path()))) Fail("invalid_output");
        if (c.dim < 0 || c.dim > .85 || c.surface < 0 || c.surface > 1 || c.introSeconds < 0 || c.introSeconds > 8 || c.maxSeconds < 0 || c.maxSeconds > 120) Fail("invalid_range");
        if (!std::isfinite(c.glassOpacity) || c.glassOpacity < .65f || c.glassOpacity > .96f || !std::isfinite(c.glassBlur) || c.glassBlur < 12 || c.glassBlur > 80) Fail("invalid_glass_range");
        if (c.kind == L"image" || c.kind == L"video" || (c.kind==L"builtin"&&c.preset==3)) {
            auto media = std::filesystem::path(c.path);
            if (!media.is_absolute() || c.path.rfind(L"\\\\", 0) == 0 || !std::filesystem::is_regular_file(media)) Fail("media_missing");
        }
        if(c.introSeconds>0&&!c.introPath.empty()) {
            auto intro=std::filesystem::path(c.introPath);
            if(!intro.is_absolute()||c.introPath.rfind(L"\\\\",0)==0||!std::filesystem::is_regular_file(intro))Fail("intro_video_missing");
        }
        if (!c.parentPid || !c.parentBirth || c.stopEvent.empty()) Fail("invalid_owner");
        if (c.mode == L"render" && (!c.target || !c.targetPid || !c.targetBirth)) Fail("invalid_client");
        if (c.mode == L"preview" && !c.parentWindow) Fail("invalid_preview");
        if(c.mode==L"guard"&&(c.title.rfind(L"CGSS.Render.",0)!=0||!std::filesystem::path(c.enginePath).is_absolute()||!std::filesystem::is_regular_file(c.enginePath)))Fail("scene_identity");
        if(c.mode==L"record"&&(c.recordSeconds<1||c.recordSeconds>60||!IsInside(c.recordPath,file.parent_path())||std::filesystem::path(c.recordPath).extension()!=L".mp4"||std::filesystem::exists(c.recordPath)||c.kind==L"builtin"))Fail("invalid_record");
        return c;
    }
};
struct Handle {
    HANDLE value = nullptr;
    Handle() = default;
    explicit Handle(HANDLE h) : value(h) {}
    Handle(Handle const&) = delete;
    Handle& operator=(Handle const&) = delete;
    ~Handle() { if (value && value != INVALID_HANDLE_VALUE) CloseHandle(value); }
};
struct Lifetime : Handle {
    Lifetime(DWORD pid, unsigned long long expected, bool official = false) {
        value = OpenProcess(SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid);
        if (!value) Fail("process_unavailable");
        FILETIME created{}, exit{}, kernel{}, user{};
        if (!GetProcessTimes(value, &created, &exit, &kernel, &user) ||
            ((static_cast<unsigned long long>(created.dwHighDateTime) << 32) | created.dwLowDateTime) != expected) Fail("process_changed");
        if (official) {
            UINT32 length = 0;
            if (GetPackageFamilyName(value, &length, nullptr) != ERROR_INSUFFICIENT_BUFFER || length > 512) Fail("client_unverified");
            std::vector<wchar_t> family(length);
            if (GetPackageFamilyName(value, &length, family.data()) != ERROR_SUCCESS) Fail("client_unverified");
            std::wstring name(family.data());
            if (name != L"OpenAI.Codex_2p2nqsd0c76g0" && name != L"OpenAI.ChatGPT-Desktop_2p2nqsd0c76g0") Fail("client_unverified");
        }
    }
    bool Alive() const { return WaitForSingleObject(value, 0) == WAIT_TIMEOUT; }
};
inline void VerifyWindow(HWND window, DWORD expectedPid) {
    DWORD pid = 0; GetWindowThreadProcessId(window, &pid);
    if (!IsWindow(window) || pid != expectedPid) Fail("window_changed");
}
// This worker owns only this helper. A blocked decoder must never leave an orphan overlay.
class Watchdog {
    Config const& c;
    Lifetime parent;
    std::unique_ptr<Lifetime> client;
    std::unique_ptr<Lifetime> source;
    Handle stop;
    std::atomic<bool> done{false};
    std::atomic<ULONGLONG> pulse{GetTickCount64()};
    std::thread worker;
public:
    explicit Watchdog(Config const& config) : c(config), parent(c.parentPid,c.parentBirth),
        stop(OpenEventW(SYNCHRONIZE,FALSE,c.stopEvent.c_str())) {
        if (!stop.value) Fail("stop_channel_missing");
        if(c.targetPid) { client=std::make_unique<Lifetime>(c.targetPid,c.targetBirth,true); VerifyWindow(c.target,c.targetPid); }
        if(c.kind==L"window") {
            if(!c.sourcePid||!c.sourceBirth||!c.sourceWindow||c.title.rfind(L"CGSS.Render.",0)!=0)Fail("scene_identity");
            source=std::make_unique<Lifetime>(c.sourcePid,c.sourceBirth);VerifyWindow(c.sourceWindow,c.sourcePid);
        }
        auto began=GetTickCount64();
        worker=std::thread([this,began]{
            while(!done.load()) {
                bool end=!parent.Alive() || (client&&!client->Alive()) || WaitForSingleObject(stop.value,0)==WAIT_OBJECT_0;
                end=end || GetTickCount64()-pulse.load()>25000 || (c.maxSeconds>0&&GetTickCount64()-began>static_cast<ULONGLONG>(c.maxSeconds*1000));
                if(end) { CloseSource(); TerminateProcess(GetCurrentProcess(),2); return; }
                Sleep(100);
            }
        });
    }
    void Pulse() { pulse.store(GetTickCount64()); }
    void CloseSource() {
        if(!source||!source->Alive())return;
        DWORD pid=0;GetWindowThreadProcessId(c.sourceWindow,&pid);wchar_t title[256]{};GetWindowTextW(c.sourceWindow,title,256);
        if(pid==c.sourcePid&&c.title==title)PostMessageW(c.sourceWindow,WM_CLOSE,0,0);
    }
    ~Watchdog() { done.store(true); if(worker.joinable())worker.join(); CloseSource(); }
};
inline std::string ReadableError(hresult_error const& error) {
    char buffer[80]{}; sprintf_s(buffer, "%s:graphics_%08lx", phase, static_cast<unsigned long>(error.code())); return buffer;
}
inline void Status(Config const& c, const wchar_t* state, int width = 0, int height = 0,
    int mediaWidth = 0, int mediaHeight = 0, unsigned long long frames = 0, DWORD hitPid = 0, const char* error = "", unsigned long long decoded = 0, double duration = 0) {
    JsonObject j;
    j.SetNamedValue(L"state", JsonValue::CreateStringValue(state));
    j.SetNamedValue(L"width", JsonValue::CreateNumberValue(width)); j.SetNamedValue(L"height", JsonValue::CreateNumberValue(height));
    j.SetNamedValue(L"sourceWidth", JsonValue::CreateNumberValue(mediaWidth)); j.SetNamedValue(L"sourceHeight", JsonValue::CreateNumberValue(mediaHeight));
    j.SetNamedValue(L"frames", JsonValue::CreateNumberValue(static_cast<double>(frames)));
    j.SetNamedValue(L"decodedFrames", JsonValue::CreateNumberValue(static_cast<double>(decoded)));
    j.SetNamedValue(L"duration", JsonValue::CreateNumberValue(duration));
    j.SetNamedValue(L"targetPid", JsonValue::CreateNumberValue(c.targetPid)); j.SetNamedValue(L"hitPid", JsonValue::CreateNumberValue(hitPid));
    j.SetNamedValue(L"error", JsonValue::CreateStringValue(to_hstring(error)));
    if(c.target)j.SetNamedValue(L"targetDpi",JsonValue::CreateNumberValue(GetDpiForWindow(c.target)));
    if(c.kind==L"window"&&c.sourceWindow){
        RECT r{};if(GetWindowRect(c.sourceWindow,&r)){
            int left=GetSystemMetrics(SM_XVIRTUALSCREEN),top=GetSystemMetrics(SM_YVIRTUALSCREEN);
            int right=left+GetSystemMetrics(SM_CXVIRTUALSCREEN),bottom=top+GetSystemMetrics(SM_CYVIRTUALSCREEN);
            j.SetNamedValue(L"sourceLeft",JsonValue::CreateNumberValue(r.left));j.SetNamedValue(L"sourceTop",JsonValue::CreateNumberValue(r.top));
            j.SetNamedValue(L"sourceRight",JsonValue::CreateNumberValue(r.right));j.SetNamedValue(L"sourceBottom",JsonValue::CreateNumberValue(r.bottom));
            j.SetNamedValue(L"sourceIsolated",JsonValue::CreateBooleanValue(r.right<=left||r.left>=right||r.bottom<=top||r.top>=bottom));
        }
    }
    auto text = to_string(j.Stringify());
    std::wstring temporary = c.status + L".tmp";
    Handle file(CreateFileW(temporary.c_str(), GENERIC_WRITE, FILE_SHARE_READ, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr));
    if (file.value == INVALID_HANDLE_VALUE) Fail("status_failed");
    DWORD written = 0;
    if (!WriteFile(file.value, text.data(), static_cast<DWORD>(text.size()), &written, nullptr) || written != text.size()) Fail("status_failed");
    CloseHandle(file.value); file.value = nullptr;
    bool moved=false;
    for(int attempt=0;attempt<10&&!moved;attempt++){moved=MoveFileExW(temporary.c_str(),c.status.c_str(),MOVEFILE_REPLACE_EXISTING)!=FALSE;if(!moved)Sleep(10);}
    if(!moved)Fail("status_failed");
}
inline int GuardScene(Config const& c) {
    Lifetime parent(c.parentPid,c.parentBirth);
    Handle stop(OpenEventW(SYNCHRONIZE,FALSE,c.stopEvent.c_str()));if(!stop.value)Fail("stop_channel_missing");
    auto root=std::filesystem::path(c.enginePath).parent_path().wstring()+L"\\";
    auto find=[&]() -> HWND {
        HWND window=FindWindowW(nullptr,c.title.c_str());if(!window)return nullptr;
        DWORD pid=0;GetWindowThreadProcessId(window,&pid);Handle process(OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION,FALSE,pid));
        if(!process.value)return nullptr;wchar_t file[32768]{};DWORD size=32768;
        if(!QueryFullProcessImageNameW(process.value,0,file,&size)||size<root.size()||_wcsnicmp(file,root.c_str(),root.size())!=0)return nullptr;
        return window;
    };
    Status(c,L"ready");
    bool stopping=false,observed=false;auto ended=Clock::now();
    while(true){
        HWND window=find();if(window)observed=true;
        if(!stopping&&(!parent.Alive()||WaitForSingleObject(stop.value,0)==WAIT_OBJECT_0)){stopping=true;ended=Clock::now();}
        if(stopping){
            if(window)PostMessageW(window,WM_CLOSE,0,0);
            else if(observed||Seconds(ended)>8)break;
            if(Seconds(ended)>12)break;
        }else if(window){
            RECT bounds{};if(GetWindowRect(window,&bounds)){
                int left=GetSystemMetrics(SM_XVIRTUALSCREEN),top=GetSystemMetrics(SM_YVIRTUALSCREEN);
                int right=left+GetSystemMetrics(SM_CXVIRTUALSCREEN),bottom=top+GetSystemMetrics(SM_CYVIRTUALSCREEN);
                if(bounds.left<right&&bounds.right>left&&bounds.top<bottom&&bounds.bottom>top){
                    int width=bounds.right-bounds.left,height=bounds.bottom-bounds.top;
                    RECT isolated{};
                    if(width<=0||height<=0||!SetWindowPos(window,HWND_BOTTOM,left-width-64,top,width,height,SWP_NOACTIVATE|SWP_NOSIZE)||!GetWindowRect(window,&isolated)||isolated.right>left){
                        PostMessageW(window,WM_CLOSE,0,0);stopping=true;ended=Clock::now();
                    }
                }
            }
        }
        Sleep(100);
    }
    Status(c,L"stopped");return 0;
}
inline void SavePng(ID3D11Device* device, ID3D11DeviceContext* context, ID3D11Texture2D* texture, std::wstring const& path, int width, int height) {
    D3D11_TEXTURE2D_DESC desc{}; texture->GetDesc(&desc);
    desc.Usage = D3D11_USAGE_STAGING; desc.BindFlags = 0; desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ; desc.MiscFlags = 0;
    com_ptr<ID3D11Texture2D> staging; check_hresult(device->CreateTexture2D(&desc, nullptr, staging.put()));
    context->CopyResource(staging.get(), texture);
    D3D11_MAPPED_SUBRESOURCE data{}; check_hresult(context->Map(staging.get(), 0, D3D11_MAP_READ, 0, &data));
    try {
        auto factory = create_instance<IWICImagingFactory>(CLSID_WICImagingFactory);
        com_ptr<IWICStream> stream; check_hresult(factory->CreateStream(stream.put()));
        check_hresult(stream->InitializeFromFilename(path.c_str(), GENERIC_WRITE));
        com_ptr<IWICBitmapEncoder> encoder; check_hresult(factory->CreateEncoder(GUID_ContainerFormatPng, nullptr, encoder.put()));
        check_hresult(encoder->Initialize(stream.get(), WICBitmapEncoderNoCache));
        com_ptr<IWICBitmapFrameEncode> frame; check_hresult(encoder->CreateNewFrame(frame.put(), nullptr));
        check_hresult(frame->Initialize(nullptr)); check_hresult(frame->SetSize(width, height));
        auto format = GUID_WICPixelFormat32bppBGRA; check_hresult(frame->SetPixelFormat(&format));
        check_hresult(frame->WritePixels(height, data.RowPitch, data.RowPitch * height, static_cast<BYTE*>(data.pData)));
        check_hresult(frame->Commit()); check_hresult(encoder->Commit());
    } catch (...) { context->Unmap(staging.get(), 0); throw; }
    context->Unmap(staging.get(), 0);
}
class Capture {
    IDirect3DDevice runtimeDevice{nullptr};
    GraphicsCaptureItem item{nullptr};
    Direct3D11CaptureFramePool pool{nullptr};
    GraphicsCaptureSession session{nullptr};
    int poolWidth=0,poolHeight=0;
public:
    com_ptr<ID3D11Texture2D> texture;
    com_ptr<ID3D11ShaderResourceView> view;
    int width = 0, height = 0;
    unsigned long long frames = 0;
    void Invalidate() { view=nullptr; texture=nullptr; for(int i=0;i<4;i++){auto stale=pool.TryGetNextFrame();if(!stale)break;stale.Close();} }
    Capture(ID3D11Device* device, HWND window) {
        com_ptr<ID3D11Device> retained; retained.copy_from(device);
        auto dxgi = retained.as<IDXGIDevice>();
        com_ptr<IInspectable> inspectable;
        check_hresult(CreateDirect3D11DeviceFromDXGIDevice(dxgi.get(), inspectable.put()));
        runtimeDevice = inspectable.as<IDirect3DDevice>();
        auto factory = get_activation_factory<GraphicsCaptureItem, IGraphicsCaptureItemInterop>();
        phase="capture_item";
        check_hresult(factory->CreateForWindow(window, guid_of<GraphicsCaptureItem>(), put_abi(item)));
        width = item.Size().Width; height = item.Size().Height;
        poolWidth=width;poolHeight=height;
        if (width <= 0 || height <= 0 || width > 16384 || height > 16384) Fail("capture_size");
        phase="capture_pool";pool = Direct3D11CaptureFramePool::CreateFreeThreaded(runtimeDevice, DirectXPixelFormat::B8G8R8A8UIntNormalized, 2, item.Size());
        session = pool.CreateCaptureSession(item);
        if (winrt::Windows::Foundation::Metadata::ApiInformation::IsPropertyPresent(L"Windows.Graphics.Capture.GraphicsCaptureSession", L"IsCursorCaptureEnabled")) session.IsCursorCaptureEnabled(false);
        phase="capture_start";session.StartCapture();
    }
    ~Capture() { if (session) session.Close(); if (pool) pool.Close(); }
    bool Update(ID3D11Device* device, ID3D11DeviceContext* context) {
        auto frame = pool.TryGetNextFrame();
        if (!frame) return false;
        // Always consume the latest frame; old-size queued frames must not reappear after a resize.
        for(int i=0;i<3;i++){auto next=pool.TryGetNextFrame();if(!next)break;frame.Close();frame=next;}
        auto size = frame.ContentSize();
        if (size.Width <= 0 || size.Height <= 0) { frame.Close(); return false; }
        if(size.Width>16384||size.Height>16384)Fail("capture_size");
        if (size.Width != poolWidth || size.Height != poolHeight) {
            poolWidth=size.Width;poolHeight=size.Height;
            texture = nullptr; view = nullptr; frame.Close();
            pool.Recreate(runtimeDevice, DirectXPixelFormat::B8G8R8A8UIntNormalized, 2, size); return false;
        }
        auto access = frame.Surface().as<::Windows::Graphics::DirectX::Direct3D11::IDirect3DDxgiInterfaceAccess>();
        com_ptr<ID3D11Texture2D> source; check_hresult(access->GetInterface(__uuidof(ID3D11Texture2D), source.put_void()));
        D3D11_TEXTURE2D_DESC desc{};source->GetDesc(&desc);
        if(desc.Width<static_cast<UINT>(size.Width)||desc.Height<static_cast<UINT>(size.Height)){frame.Close();Invalidate();return false;}
        if (!texture || width!=size.Width || height!=size.Height) {
            view=nullptr;texture=nullptr;
            desc.Width=size.Width;desc.Height=size.Height;desc.MipLevels=1;desc.ArraySize=1;
            desc.BindFlags = D3D11_BIND_SHADER_RESOURCE; desc.Usage = D3D11_USAGE_DEFAULT; desc.CPUAccessFlags = 0; desc.MiscFlags = 0;
            check_hresult(device->CreateTexture2D(&desc, nullptr, texture.put()));
            check_hresult(device->CreateShaderResourceView(texture.get(), nullptr, view.put()));
        }
        // ContentSize can be smaller than the backing pool surface during rapid resizing.
        D3D11_BOX box{0,0,0,static_cast<UINT>(size.Width),static_cast<UINT>(size.Height),1};
        context->CopySubresourceRegion(texture.get(),0,0,0,0,source.get(),0,&box);
        width=size.Width;height=size.Height;frame.Close(); frames++; return true;
    }
};
}
