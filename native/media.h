#pragma once
#include "support.h"
namespace cgss {
class Media {
    ID3D11Device* device;
    ID3D11DeviceContext* context;
    com_ptr<ID3D11Texture2D> texture;
    com_ptr<IMFSourceReader> reader;
    com_ptr<IMFSample> nextSample;
    com_ptr<IMFDXGIDeviceManager> manager;
    com_ptr<ID3D11VideoDevice> videoDevice;
    com_ptr<ID3D11VideoContext> videoContext;
    com_ptr<ID3D11VideoProcessorEnumerator> videoEnumerator;
    com_ptr<ID3D11VideoProcessor> videoProcessor;
    com_ptr<ID3D11VideoProcessorOutputView> videoOutput;
    std::unique_ptr<Capture> capture;
    std::unique_ptr<Lifetime> lifetime;
    LONGLONG nextTime = 0;
    LONG stride = 0;
    Clock::time_point epoch = Clock::now();
    std::vector<BYTE> pixels;
    std::wstring kind;
    bool hardware=false,paused=false; Clock::time_point pausedAt;
    HWND window=nullptr; DWORD windowPid=0; std::wstring windowTitle;
public:
    int width = 1, height = 1;
    unsigned long long decoded = 0;
    double duration = 0;
    com_ptr<ID3D11ShaderResourceView> view;
    Media(ID3D11Device* d, ID3D11DeviceContext* c, Config const& config) : device(d), context(c), kind(config.kind) {
        if (kind == L"builtin") {
            if(config.preset==3){LoadImage(config.path);return;}
            BYTE pixel[4]{0,0,0,255}; Create(1, 1, pixel); return;
        }
        if (kind == L"image") { LoadImage(config.path); return; }
        if (kind == L"video") { OpenVideo(config.path); return; }
        if (kind == L"window") {
            if (!config.sourcePid || !config.sourceBirth || !config.sourceWindow || config.title.rfind(L"CGSS.Render.", 0) != 0) Fail("scene_identity");
            lifetime = std::make_unique<Lifetime>(config.sourcePid, config.sourceBirth);
            VerifyWindow(config.sourceWindow, config.sourcePid);
            wchar_t title[256]{}; GetWindowTextW(config.sourceWindow, title, 256);
            if (config.title != title) Fail("scene_identity");
            window=config.sourceWindow; windowPid=config.sourcePid; windowTitle=config.title;
            capture = std::make_unique<Capture>(device, config.sourceWindow);
        }
    }
    bool Update() {
        if(paused){epoch+=Clock::now()-pausedAt;paused=false;}
        if (capture) {
            if (!lifetime->Alive()) Fail("scene_closed");
            VerifyWindow(window,windowPid); wchar_t title[256]{}; GetWindowTextW(window,title,256);
            if(windowTitle!=title)Fail("scene_identity");
            bool changed = capture->Update(device, context);
            view = capture->view; width = capture->width; height = capture->height;
            if (changed) decoded++; return changed;
        }
        if (!reader) return false;
        bool changed = false;
        LONGLONG elapsed = static_cast<LONGLONG>(Seconds(epoch) * 10000000);
        for (int i = 0; i < 4 && nextSample && nextTime <= elapsed; ++i) {
            Upload(nextSample.get()); changed = true;
            if (!ReadNext()) {
                PROPVARIANT position{}; position.vt = VT_I8; position.hVal.QuadPart = 0;
                check_hresult(reader->SetCurrentPosition(GUID_NULL, position));
                epoch = Clock::now(); elapsed = 0;
                if (!ReadNext()) Fail("video_empty");
            }
        }
        return changed;
    }
    ID3D11Texture2D* Texture() const { return capture ? capture->texture.get() : texture.get(); }
    void Suspend(){if(!paused){paused=true;pausedAt=Clock::now();}}
private:
    void Create(UINT w, UINT h, BYTE const* data) {
        if (!w || !h || w > 16384 || h > 16384 || static_cast<unsigned long long>(w)*h > 64000000) Fail("media_size");
        width = static_cast<int>(w); height = static_cast<int>(h);
        D3D11_TEXTURE2D_DESC desc{};
        desc.Width = w; desc.Height = h; desc.MipLevels = 1; desc.ArraySize = 1;
        desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM; desc.SampleDesc.Count = 1;
        desc.Usage = D3D11_USAGE_DEFAULT; desc.BindFlags = D3D11_BIND_SHADER_RESOURCE;
        D3D11_SUBRESOURCE_DATA initial{data, w*4, 0};
        check_hresult(device->CreateTexture2D(&desc, data ? &initial : nullptr, texture.put()));
        check_hresult(device->CreateShaderResourceView(texture.get(), nullptr, view.put()));
    }
    void LoadImage(std::wstring const& path) {
        auto factory = create_instance<IWICImagingFactory>(CLSID_WICImagingFactory);
        com_ptr<IWICBitmapDecoder> decoder;
        check_hresult(factory->CreateDecoderFromFilename(path.c_str(), nullptr, GENERIC_READ, WICDecodeMetadataCacheOnLoad, decoder.put()));
        com_ptr<IWICBitmapFrameDecode> frame; check_hresult(decoder->GetFrame(0, frame.put()));
        UINT w = 0, h = 0; check_hresult(frame->GetSize(&w,&h));
        if (!w || !h || w>16384 || h>16384 || static_cast<unsigned long long>(w)*h>64000000) Fail("media_size");
        com_ptr<IWICFormatConverter> converter; check_hresult(factory->CreateFormatConverter(converter.put()));
        check_hresult(converter->Initialize(frame.get(), GUID_WICPixelFormat32bppBGRA, WICBitmapDitherTypeNone, nullptr, 0, WICBitmapPaletteTypeCustom));
        pixels.resize(static_cast<size_t>(w)*h*4);
        check_hresult(converter->CopyPixels(nullptr,w*4,static_cast<UINT>(pixels.size()),pixels.data()));
        Create(w,h,pixels.data()); pixels.clear(); decoded = 1;
    }
    void OpenVideo(std::wstring const& path) {
        try { OpenVideoCore(path,true); }
        catch(...) {
            reader=nullptr;nextSample=nullptr;manager=nullptr;videoOutput=nullptr;videoProcessor=nullptr;videoEnumerator=nullptr;
            videoDevice=nullptr;videoContext=nullptr;view=nullptr;texture=nullptr;hardware=false;decoded=0;
            OpenVideoCore(path,false);
        }
    }
    void OpenVideoCore(std::wstring const& path,bool gpu) {
        com_ptr<IMFAttributes> attributes; check_hresult(MFCreateAttributes(attributes.put(),4));
        check_hresult(attributes->SetUINT32(MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS,TRUE));
        if(gpu){
            UINT token=0;check_hresult(MFCreateDXGIDeviceManager(&token,manager.put()));check_hresult(manager->ResetDevice(device,token));
            check_hresult(attributes->SetUnknown(MF_SOURCE_READER_D3D_MANAGER,manager.get()));
            check_hresult(attributes->SetUINT32(MF_SOURCE_READER_DISABLE_DXVA,FALSE));
        }else check_hresult(attributes->SetUINT32(MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING,TRUE));
        check_hresult(MFCreateSourceReaderFromURL(path.c_str(),attributes.get(),reader.put()));
        PROPVARIANT length{};
        if(SUCCEEDED(reader->GetPresentationAttribute(MF_SOURCE_READER_MEDIASOURCE,MF_PD_DURATION,&length))&&length.vt==VT_UI8)duration=length.uhVal.QuadPart/10000000.;
        PropVariantClear(&length);
        check_hresult(reader->SetStreamSelection(MF_SOURCE_READER_ALL_STREAMS,FALSE));
        check_hresult(reader->SetStreamSelection(MF_SOURCE_READER_FIRST_VIDEO_STREAM,TRUE));
        com_ptr<IMFMediaType> desired; check_hresult(MFCreateMediaType(desired.put()));
        check_hresult(desired->SetGUID(MF_MT_MAJOR_TYPE,MFMediaType_Video)); check_hresult(desired->SetGUID(MF_MT_SUBTYPE,gpu?MFVideoFormat_NV12:MFVideoFormat_RGB32));
        check_hresult(reader->SetCurrentMediaType(MF_SOURCE_READER_FIRST_VIDEO_STREAM,nullptr,desired.get()));
        com_ptr<IMFMediaType> actual; check_hresult(reader->GetCurrentMediaType(MF_SOURCE_READER_FIRST_VIDEO_STREAM,actual.put()));
        UINT32 w = 0, h = 0; check_hresult(MFGetAttributeSize(actual.get(),MF_MT_FRAME_SIZE,&w,&h));
        if(gpu){
            width=w;height=h;if(!w||!h||w>16384||h>16384||static_cast<unsigned long long>(w)*h>64000000)Fail("media_size");
            InitVideoProcessor();hardware=true;
        }else{
            UINT32 stored = 0;
            if (SUCCEEDED(actual->GetUINT32(MF_MT_DEFAULT_STRIDE,&stored))) stride = static_cast<LONG>(stored);
            else check_hresult(MFGetStrideForBitmapInfoHeader(MFVideoFormat_RGB32.Data1,w,&stride));
            Create(w,h,nullptr); pixels.resize(static_cast<size_t>(w)*h*4);
        }
        if (!ReadNext()) Fail("video_empty");
        Upload(nextSample.get()); nextSample = nullptr;
        ReadNext(); epoch = Clock::now();
    }
    void InitVideoProcessor(){
        check_hresult(device->QueryInterface(__uuidof(ID3D11VideoDevice),videoDevice.put_void()));
        check_hresult(context->QueryInterface(__uuidof(ID3D11VideoContext),videoContext.put_void()));
        D3D11_VIDEO_PROCESSOR_CONTENT_DESC content{};content.InputFrameFormat=D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE;
        content.InputFrameRate={60,1};content.OutputFrameRate={60,1};content.InputWidth=content.OutputWidth=width;content.InputHeight=content.OutputHeight=height;
        content.Usage=D3D11_VIDEO_USAGE_PLAYBACK_NORMAL;
        check_hresult(videoDevice->CreateVideoProcessorEnumerator(&content,videoEnumerator.put()));
        check_hresult(videoDevice->CreateVideoProcessor(videoEnumerator.get(),0,videoProcessor.put()));
        D3D11_TEXTURE2D_DESC d{};d.Width=width;d.Height=height;d.MipLevels=1;d.ArraySize=1;d.SampleDesc.Count=1;
        d.Format=DXGI_FORMAT_B8G8R8A8_UNORM;d.BindFlags=D3D11_BIND_RENDER_TARGET|D3D11_BIND_SHADER_RESOURCE;
        check_hresult(device->CreateTexture2D(&d,nullptr,texture.put()));check_hresult(device->CreateShaderResourceView(texture.get(),nullptr,view.put()));
        D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC out{};out.ViewDimension=D3D11_VPOV_DIMENSION_TEXTURE2D;
        check_hresult(videoDevice->CreateVideoProcessorOutputView(texture.get(),videoEnumerator.get(),&out,videoOutput.put()));
        RECT rect{0,0,width,height};
        videoContext->VideoProcessorSetStreamFrameFormat(videoProcessor.get(),0,D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE);
        videoContext->VideoProcessorSetStreamSourceRect(videoProcessor.get(),0,TRUE,&rect);
        videoContext->VideoProcessorSetStreamDestRect(videoProcessor.get(),0,TRUE,&rect);
        videoContext->VideoProcessorSetOutputTargetRect(videoProcessor.get(),TRUE,&rect);
        videoContext->VideoProcessorSetStreamAutoProcessingMode(videoProcessor.get(),0,FALSE);
        videoContext->VideoProcessorSetOutputAlphaFillMode(videoProcessor.get(),D3D11_VIDEO_PROCESSOR_ALPHA_FILL_MODE_OPAQUE,0);
        D3D11_VIDEO_PROCESSOR_COLOR_SPACE space{};space.YCbCr_Matrix=1;space.Nominal_Range=2;
        videoContext->VideoProcessorSetStreamColorSpace(videoProcessor.get(),0,&space);
        space.Nominal_Range=1;videoContext->VideoProcessorSetOutputColorSpace(videoProcessor.get(),&space);
    }
    bool ReadNext() {
        nextSample = nullptr;
        for (int i=0; i<60; ++i) {
            DWORD flags = 0;
            check_hresult(reader->ReadSample(MF_SOURCE_READER_FIRST_VIDEO_STREAM,0,nullptr,&flags,&nextTime,nextSample.put()));
            if (flags & MF_SOURCE_READERF_ERROR) Fail("video_decode");
            if (flags & MF_SOURCE_READERF_CURRENTMEDIATYPECHANGED) {
                com_ptr<IMFMediaType> actual; check_hresult(reader->GetCurrentMediaType(MF_SOURCE_READER_FIRST_VIDEO_STREAM,actual.put()));
                UINT32 w=0,h=0; check_hresult(MFGetAttributeSize(actual.get(),MF_MT_FRAME_SIZE,&w,&h));
                if (w != width || h != height) Fail("video_format_changed");
            }
            if (flags & MF_SOURCE_READERF_ENDOFSTREAM) return false;
            if (nextSample) return true;
        }
        Fail("video_decode"); return false;
    }
    void Upload(IMFSample* sample) {
        if(hardware){
            com_ptr<IMFMediaBuffer> buffer;check_hresult(sample->GetBufferByIndex(0,buffer.put()));
            auto dxgi=buffer.as<IMFDXGIBuffer>();com_ptr<ID3D11Texture2D> decodedTexture;
            check_hresult(dxgi->GetResource(__uuidof(ID3D11Texture2D),decodedTexture.put_void()));UINT index=0;check_hresult(dxgi->GetSubresourceIndex(&index));
            D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC input{};input.ViewDimension=D3D11_VPIV_DIMENSION_TEXTURE2D;input.Texture2D.ArraySlice=index;
            com_ptr<ID3D11VideoProcessorInputView> inputView;
            check_hresult(videoDevice->CreateVideoProcessorInputView(decodedTexture.get(),videoEnumerator.get(),&input,inputView.put()));
            D3D11_VIDEO_PROCESSOR_STREAM stream{};stream.Enable=TRUE;stream.pInputSurface=inputView.get();
            check_hresult(videoContext->VideoProcessorBlt(videoProcessor.get(),videoOutput.get(),0,1,&stream));decoded++;return;
        }
        com_ptr<IMFMediaBuffer> buffer; check_hresult(sample->ConvertToContiguousBuffer(buffer.put()));
        BYTE* first = nullptr; LONG pitch = stride;
        com_ptr<IMF2DBuffer> two;
        bool locked2D = SUCCEEDED(buffer->QueryInterface(__uuidof(IMF2DBuffer),two.put_void()));
        DWORD length = 0;
        if (locked2D) check_hresult(two->Lock2D(&first,&pitch));
        else {
            check_hresult(buffer->Lock(&first,nullptr,&length));
            size_t needed = static_cast<size_t>(std::abs(pitch))*(height-1)+static_cast<size_t>(width)*4;
            if (needed>length) { buffer->Unlock(); Fail("video_stride"); }
            if (pitch<0) first += static_cast<size_t>(-pitch)*(height-1);
        }
        if (std::abs(pitch)<width*4) { if (locked2D) two->Unlock2D(); else buffer->Unlock(); Fail("video_stride"); }
        for (int y=0;y<height;++y) memcpy(pixels.data()+static_cast<size_t>(y)*width*4,first+static_cast<ptrdiff_t>(y)*pitch,static_cast<size_t>(width)*4);
        if (locked2D) two->Unlock2D(); else buffer->Unlock();
        for (size_t i=3;i<pixels.size();i+=4) pixels[i]=255;
        context->UpdateSubresource(texture.get(),0,nullptr,pixels.data(),width*4,0); decoded++;
    }
};
}
