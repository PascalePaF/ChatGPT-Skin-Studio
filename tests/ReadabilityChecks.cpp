#define NOMINMAX
#include <windows.h>
#include <d3d11.h>
#include <d3dcompiler.h>
#include <wrl/client.h>
#include <algorithm>
#include <array>
#include <cmath>
#include <cstdio>
#include <filesystem>
#include <stdexcept>
#include <vector>
using Microsoft::WRL::ComPtr;
struct Pixel { unsigned char r,g,b,a=255; };
void check(HRESULT hr){if(FAILED(hr))throw std::runtime_error("D3D rendering failed");}
double channel(unsigned char c){double v=c/255.;return v<=.04045?v/12.92:std::pow((v+.055)/1.055,2.4);}
double luminance(Pixel p){return .2126*channel(p.r)+.7152*channel(p.g)+.0722*channel(p.b);}
double contrast(Pixel a,Pixel b){double x=luminance(a),y=luminance(b);return (std::max(x,y)+.05)/(std::min(x,y)+.05);}
int difference(Pixel a,Pixel b){return std::max({std::abs(int(a.r)-b.r),std::abs(int(a.g)-b.g),std::abs(int(a.b)-b.b)});}
int wmain(int argc,wchar_t** argv){try{
 if(argc<2||argc>3)throw std::runtime_error("shader directory required");
 bool fidelity=argc==3&&std::wstring(argv[2])==L"--fidelity";
 ComPtr<ID3D11Device> device;ComPtr<ID3D11DeviceContext> context;
 auto hr=D3D11CreateDevice(nullptr,D3D_DRIVER_TYPE_HARDWARE,nullptr,0,nullptr,0,D3D11_SDK_VERSION,&device,nullptr,&context);
 if(FAILED(hr))check(D3D11CreateDevice(nullptr,D3D_DRIVER_TYPE_WARP,nullptr,0,nullptr,0,D3D11_SDK_VERSION,&device,nullptr,&context));
 ComPtr<ID3DBlob> vertex,pixel;auto root=std::filesystem::path(argv[1]);
 check(D3DReadFileToBlob((root/L"VS.shader").c_str(),&vertex));check(D3DReadFileToBlob((root/L"PS.shader").c_str(),&pixel));
 ComPtr<ID3D11VertexShader> vs;ComPtr<ID3D11PixelShader> ps;
 check(device->CreateVertexShader(vertex->GetBufferPointer(),vertex->GetBufferSize(),nullptr,&vs));check(device->CreatePixelShader(pixel->GetBufferPointer(),pixel->GetBufferSize(),nullptr,&ps));
 D3D11_SAMPLER_DESC sd{};sd.Filter=D3D11_FILTER_MIN_MAG_MIP_LINEAR;sd.AddressU=sd.AddressV=sd.AddressW=D3D11_TEXTURE_ADDRESS_CLAMP;sd.MaxLOD=D3D11_FLOAT32_MAX;
 ComPtr<ID3D11SamplerState> sampler;check(device->CreateSamplerState(&sd,&sampler));
 std::array<Pixel,7> walls{{{0,0,0},{255,255,255},{245,20,20},{10,240,30},{10,20,250},{180,170,150},{20,30,40}}};
 int cases=0;double minimum=100;
 for(auto size:std::array<std::array<int,2>,3>{{{256,160},{400,160},{256,240}}})for(int light=0;light<=1;light++)for(int rail=0;rail<3;rail++)for(auto wallpaper:walls){
  if(fidelity&&(wallpaper.r||wallpaper.g||wallpaper.b))continue;
  int width=size[0],height=size[1];Pixel base=light?Pixel{244,244,244}:Pixel{20,20,20};
  std::vector<Pixel> ui(width*height,base),art(width*height,fidelity?base:wallpaper);
  auto rect=[&](int x,int y,int w,int h,Pixel color){for(int yy=y;yy<y+h;yy++)for(int xx=x;xx<x+w;xx++)ui[yy*width+xx]=color;};
  auto artRect=[&](int x,int y,int w,int h,Pixel color){for(int yy=y;yy<y+h;yy++)for(int xx=x;xx<x+w;xx++)art[yy*width+xx]=color;};
  // The navigation rail can keep its own palette when the conversation switches theme.
  bool railLight=rail==2||(rail==0&&light);Pixel railBase=railLight?Pixel{244,244,244}:Pixel{20,20,20};
  if(rail){rect(0,0,40,height,railBase);if(fidelity)artRect(0,0,40,height,railBase);}
  int railX=int(width*.035);rect(railX-2,84,5,2,railLight?Pixel{145,145,145}:Pixel{120,120,120});
  // Realistic thin glyph strokes: nearby pixels remain the original surface, including a muted label.
  rect(80,42,50,2,light?Pixel{45,45,45}:Pixel{235,235,235});
  rect(80,64,50,2,light?Pixel{170,170,170}:Pixel{95,95,95});
  rect(70,110,100,20,light?Pixel{227,227,227}:Pixel{42,42,42});
  if(fidelity)artRect(70,110,100,20,light?Pixel{227,227,227}:Pixel{42,42,42});
  rect(80,118,50,2,light?Pixel{145,145,145}:Pixel{120,120,120});
  if(fidelity){rect(180,36,30,16,light?Pixel{45,45,45}:Pixel{235,235,235});rect(80,80,50,1,light?Pixel{190,190,190}:Pixel{70,70,70});}
  Pixel photo{220,60,30};rect(180,70,30,25,photo);
  Pixel avatar{105,105,105};rect(16,height-24,8,8,avatar);
  // Deliberately corrupt the legacy single theme sample. The majority must still identify the mode.
  rect(int(width*.012),int(height*.12),2,2,light?Pixel{0,0,0}:Pixel{255,255,255});
  auto texture=[&](std::vector<Pixel>const& source,ComPtr<ID3D11ShaderResourceView>& view){
   D3D11_TEXTURE2D_DESC desc{};desc.Width=width;desc.Height=height;desc.MipLevels=desc.ArraySize=1;desc.Format=DXGI_FORMAT_R8G8B8A8_UNORM;desc.SampleDesc.Count=1;desc.BindFlags=D3D11_BIND_SHADER_RESOURCE;
   D3D11_SUBRESOURCE_DATA input{source.data(),UINT(width*4),0};ComPtr<ID3D11Texture2D> tex;check(device->CreateTexture2D(&desc,&input,&tex));check(device->CreateShaderResourceView(tex.Get(),nullptr,&view));
  };
  ComPtr<ID3D11ShaderResourceView> uiView,artView;texture(ui,uiView);texture(art,artView);
  D3D11_TEXTURE2D_DESC desc{};desc.Width=width;desc.Height=height;desc.MipLevels=desc.ArraySize=1;desc.Format=DXGI_FORMAT_R8G8B8A8_UNORM;desc.SampleDesc.Count=1;desc.BindFlags=D3D11_BIND_RENDER_TARGET;
  ComPtr<ID3D11Texture2D> output;ComPtr<ID3D11RenderTargetView> target;check(device->CreateTexture2D(&desc,nullptr,&output));check(device->CreateRenderTargetView(output.Get(),nullptr,&target));
  float data[20]{.38f,.72f,0,0,float(width),float(height),0,0,0,float(width),float(height),0,20,float(height-20),9,1,.86f,40,0,0};
  if(fidelity)data[0]=data[1]=0;
  D3D11_BUFFER_DESC cb{};cb.ByteWidth=sizeof(data);cb.BindFlags=D3D11_BIND_CONSTANT_BUFFER;D3D11_SUBRESOURCE_DATA initial{data,0,0};ComPtr<ID3D11Buffer> constants;check(device->CreateBuffer(&cb,&initial,&constants));
  ID3D11RenderTargetView* rt=target.Get();context->OMSetRenderTargets(1,&rt,nullptr);D3D11_VIEWPORT viewport{0,0,float(width),float(height),0,1};context->RSSetViewports(1,&viewport);
  context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);context->VSSetShader(vs.Get(),nullptr,0);context->PSSetShader(ps.Get(),nullptr,0);
  ID3D11ShaderResourceView* views[]{uiView.Get(),artView.Get(),artView.Get(),artView.Get(),artView.Get()};context->PSSetShaderResources(0,5,views);
  auto s=sampler.Get();context->PSSetSamplers(0,1,&s);auto b=constants.Get();context->PSSetConstantBuffers(0,1,&b);context->Draw(3,0);
  desc.BindFlags=0;desc.Usage=D3D11_USAGE_STAGING;desc.CPUAccessFlags=D3D11_CPU_ACCESS_READ;ComPtr<ID3D11Texture2D> readback;check(device->CreateTexture2D(&desc,nullptr,&readback));context->CopyResource(readback.Get(),output.Get());
  D3D11_MAPPED_SUBRESOURCE mapped{};check(context->Map(readback.Get(),0,D3D11_MAP_READ,0,&mapped));
  auto at=[&](int x,int y){return *reinterpret_cast<Pixel*>(static_cast<unsigned char*>(mapped.pData)+y*mapped.RowPitch+x*4);};
  if(fidelity){
   int delta=0;
   auto compare=[&](int x,int y,int w,int h){for(int yy=y;yy<y+h;yy++)for(int xx=x;xx<x+w;xx++)delta=std::max(delta,difference(at(xx,yy),ui[yy*width+xx]));};
   compare(80,42,50,2);compare(80,64,50,2);compare(80,118,50,2);compare(80,80,50,1);
   compare(railX-2,84,5,2);compare(180,36,30,16);compare(180,70,30,25);compare(16,height-24,8,8);
   if(delta>1){std::printf("FAIL source-pixel fidelity %s %dx%d rail=%d max channel delta=%d\n",light?"light":"dark",width,height,rail,delta);return 1;}
   context->Unmap(readback.Get(),0);ID3D11ShaderResourceView* empty[5]{};context->PSSetShaderResources(0,5,empty);context->OMSetRenderTargets(0,nullptr,nullptr);cases++;continue;
  }
  double mainRatio=contrast(at(100,42),at(100,48)),mutedRatio=contrast(at(100,64),at(100,72)),panelRatio=contrast(at(100,118),at(100,124)),railRatio=contrast(at(railX,84),at(railX,92));
  minimum=std::min({minimum,mainRatio,mutedRatio,panelRatio,railRatio});
  if(std::min({mainRatio,mutedRatio,panelRatio,railRatio})<4.5){std::printf("FAIL contrast %s %dx%d rail=%d wall(%d,%d,%d) body=%.2f muted=%.2f panel=%.2f rail=%.2f\n",light?"light":"dark",width,height,rail,wallpaper.r,wallpaper.g,wallpaper.b,mainRatio,mutedRatio,panelRatio,railRatio);return 1;}
  if(difference(at(100,48),base)<3||difference(at(railX,92),railBase)<3)throw std::runtime_error("wallpaper did not replace the conversation and navigation backgrounds");
  if(difference(at(195,80),photo)>1||difference(at(20,height-20),avatar)>1)throw std::runtime_error("photo or avatar pixels changed");
  context->Unmap(readback.Get(),0);ID3D11ShaderResourceView* empty[5]{};context->PSSetShaderResources(0,5,empty);context->OMSetRenderTargets(0,nullptr,nullptr);cases++;
 }
 if(fidelity){std::printf("PASS %d source-pixel fidelity cases: light/dark, mixed navigation palettes and 3 window ratios.\n",cases);std::puts("PASS foreground identity with matching backgrounds: thin, muted, thick and antialiased text, panel text, color images and avatars retain original RGB (maximum channel delta <= 1). Panel boundary shading is outside this identity assertion. No desktop capture or window.");return 0;}
 std::printf("PASS %d GPU composition cases: light/dark, 3 navigation palettes, 7 wallpaper colors, 3 window ratios.\n",cases);
 std::printf("PASS body, muted text and panel text >= 4.5:1 contrast; minimum %.2f:1.\n",minimum);
 std::puts("PASS wallpaper replaces both conversation and rail backgrounds even with mixed light/dark chrome.");
 std::puts("PASS avatar and color-image pixels preserved; corrupt legacy theme sample ignored. No desktop window or user content was captured.");return 0;
 }catch(std::exception const& ex){std::fprintf(stderr,"FAIL %s\n",ex.what());return 1;}}
