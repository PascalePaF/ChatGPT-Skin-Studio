#pragma once
#include "media.h"
namespace cgss {
// Encode only the verified source surface. No desktop capture or application UI is used.
class VideoRecorder {
    com_ptr<IMFSinkWriter> writer;
    com_ptr<ID3D11Texture2D> staging;
    DWORD stream = 0;
    int sourceWidth, sourceHeight;
public:
    const int width, height;
    VideoRecorder(ID3D11Device* device, ID3D11Texture2D* texture, std::wstring const& path, int w, int h)
        : sourceWidth(w), sourceHeight(h), width((w+1)&~1), height((h+1)&~1) {
        if(w<2||h<2||width>4096||height>4096||static_cast<uint64_t>(width)*height>8847360)Fail("record_size");
        phase="record_encoder";
        com_ptr<IMFAttributes> attributes;check_hresult(MFCreateAttributes(attributes.put(),2));
        check_hresult(attributes->SetUINT32(MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS,TRUE));
        check_hresult(attributes->SetUINT32(MF_SINK_WRITER_DISABLE_THROTTLING,TRUE));
        check_hresult(MFCreateSinkWriterFromURL(path.c_str(),nullptr,attributes.get(),writer.put()));
        com_ptr<IMFMediaType> encoded;check_hresult(MFCreateMediaType(encoded.put()));
        check_hresult(encoded->SetGUID(MF_MT_MAJOR_TYPE,MFMediaType_Video));check_hresult(encoded->SetGUID(MF_MT_SUBTYPE,MFVideoFormat_H264));
        check_hresult(encoded->SetUINT32(MF_MT_AVG_BITRATE,static_cast<UINT32>(std::clamp(static_cast<uint64_t>(width)*height*5,uint64_t(12000000),uint64_t(40000000)))));
        check_hresult(encoded->SetUINT32(MF_MT_INTERLACE_MODE,MFVideoInterlace_Progressive));
        check_hresult(encoded->SetUINT32(MF_MT_MPEG2_PROFILE,100));
        check_hresult(MFSetAttributeSize(encoded.get(),MF_MT_FRAME_SIZE,width,height));
        check_hresult(MFSetAttributeRatio(encoded.get(),MF_MT_FRAME_RATE,30,1));
        check_hresult(MFSetAttributeRatio(encoded.get(),MF_MT_PIXEL_ASPECT_RATIO,1,1));
        check_hresult(writer->AddStream(encoded.get(),&stream));
        com_ptr<IMFMediaType> raw;check_hresult(MFCreateMediaType(raw.put()));
        check_hresult(raw->SetGUID(MF_MT_MAJOR_TYPE,MFMediaType_Video));check_hresult(raw->SetGUID(MF_MT_SUBTYPE,MFVideoFormat_NV12));
        check_hresult(raw->SetUINT32(MF_MT_INTERLACE_MODE,MFVideoInterlace_Progressive));
        check_hresult(raw->SetUINT32(MF_MT_YUV_MATRIX,MFVideoTransferMatrix_BT709));
        check_hresult(raw->SetUINT32(MF_MT_VIDEO_NOMINAL_RANGE,MFNominalRange_16_235));
        check_hresult(MFSetAttributeSize(raw.get(),MF_MT_FRAME_SIZE,width,height));
        check_hresult(MFSetAttributeRatio(raw.get(),MF_MT_FRAME_RATE,30,1));
        check_hresult(MFSetAttributeRatio(raw.get(),MF_MT_PIXEL_ASPECT_RATIO,1,1));
        check_hresult(writer->SetInputMediaType(stream,raw.get(),nullptr));check_hresult(writer->BeginWriting());
        D3D11_TEXTURE2D_DESC desc{};texture->GetDesc(&desc);desc.Usage=D3D11_USAGE_STAGING;desc.BindFlags=0;desc.CPUAccessFlags=D3D11_CPU_ACCESS_READ;desc.MiscFlags=0;
        check_hresult(device->CreateTexture2D(&desc,nullptr,staging.put()));
    }
    void Write(ID3D11DeviceContext* context,ID3D11Texture2D* texture,LONGLONG time,LONGLONG duration) {
        phase="record_frame";context->CopyResource(staging.get(),texture);
        D3D11_MAPPED_SUBRESOURCE mapped{};check_hresult(context->Map(staging.get(),0,D3D11_MAP_READ,0,&mapped));
        com_ptr<IMFMediaBuffer> buffer;BYTE* pixels=nullptr;bool locked=false;
        try {
            DWORD bytes=width*height*3/2;check_hresult(MFCreateMemoryBuffer(bytes,buffer.put()));check_hresult(buffer->Lock(&pixels,nullptr,nullptr));locked=true;
            auto read=[&](int x,int y)->BYTE const*{return static_cast<BYTE const*>(mapped.pData)+static_cast<size_t>(std::min(y,sourceHeight-1))*mapped.RowPitch+std::min(x,sourceWidth-1)*4;};
            for(int y=0;y<height;y++)for(int x=0;x<width;x++){
                auto p=read(x,y);pixels[static_cast<size_t>(y)*width+x]=static_cast<BYTE>(16+((47*p[2]+157*p[1]+16*p[0]+128)>>8));
            }
            BYTE* chroma=pixels+static_cast<size_t>(width)*height;
            for(int y=0;y<height;y+=2)for(int x=0;x<width;x+=2){
                int r=0,g=0,b=0;for(int dy=0;dy<2;dy++)for(int dx=0;dx<2;dx++){auto p=read(x+dx,y+dy);r+=p[2];g+=p[1];b+=p[0];}
                size_t offset=static_cast<size_t>(y/2)*width+x;
                chroma[offset]=static_cast<BYTE>(std::clamp(128+((-26*r-87*g+112*b+512)>>10),16,240));
                chroma[offset+1]=static_cast<BYTE>(std::clamp(128+((112*r-102*g-10*b+512)>>10),16,240));
            }
            check_hresult(buffer->Unlock());locked=false;context->Unmap(staging.get(),0);
        }catch(...){if(locked)buffer->Unlock();context->Unmap(staging.get(),0);throw;}
        check_hresult(buffer->SetCurrentLength(width*height*3/2));
        com_ptr<IMFSample> sample;check_hresult(MFCreateSample(sample.put()));check_hresult(sample->AddBuffer(buffer.get()));
        check_hresult(sample->SetSampleTime(time));check_hresult(sample->SetSampleDuration(duration));check_hresult(writer->WriteSample(stream,sample.get()));
    }
    void Finish(){phase="record_finalize";check_hresult(writer->Finalize());writer=nullptr;}
};
}
