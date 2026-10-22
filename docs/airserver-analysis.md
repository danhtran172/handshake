# AirServer (Windows Desktop Edition): phân tích hộp đen

Mục tiêu: hiểu AirServer biến PC thành bộ nhận AirPlay / Google Cast / Miracast như thế nào, để thiết kế chiều **iPhone → PC** cho app.

## Đối tượng và giới hạn

| | |
|---|---|
| Gói | `F3F176BD.AirServer_2025.2.20.2_x64__p8qzvses5c8me` (Microsoft Store, App Dynamic ehf.) |
| Thư mục | `C:\Program Files\WindowsApps\F3F176BD.AirServer_2025.2.20.2_x64__p8qzvses5c8me` |
| Build | MSVC 19.37 (VS 2022), UWP/`windows.desktop`, TargetRuntime Native |

**Không phân tích được mã máy.** `AirServer.exe` và các DLL mang cờ `Encrypted`: đây là lớp mã hoá DRM của gói Store, chỉ giải mã cho tiến trình app có license. Tôi **không** vượt qua lớp này (dump bộ nhớ, phá DRM). Mọi kết luận dưới đây đến từ:

- Manifest đọc được (`AppxManifest.xml`, `NetworkManifest.xml`).
- Quan sát runtime trên máy này (22/10/2026): danh sách module, cổng đang mở, gói mDNS quảng bá.
- Kiến thức công khai về giao thức AirPlay. Phần này đánh dấu *(suy luận)*.

Ký hiệu: ✅ = quan sát trực tiếp, *(suy luận)* = chưa kiểm chứng trên traffic thật.

---

## 1. Thành phần (✅ manifest + module đã nạp)

- **UI:** Qt 5 static (plugin `ad-qpa-winrt`, `ad-wasapi`, `ad-winmediaengine`, `ad-uwpgamepad`), XAML host.
- **Giải mã video:**
  - FFmpeg 5.x dùng dạng DLL: `avcodec-59.dll`, `avutil-57.dll`, `avfilter-8.dll`.
  - Media Foundation (`MFPlat.dll`), D3D11/DXGI, driver phần cứng AMD (`amdxx64`, `atidxx64`, `amdenc64`).
  - *(suy luận)* Giải mã H.264/HEVC bằng phần cứng, FFmpeg làm dự phòng hoặc xử lý audio.
- **Âm thanh:** WASAPI qua Qt Multimedia.
- **Mạng và crypto:** WinSock, `bcrypt`/`ncrypt`/`CryptoWinRT`, `schannel` (TLS cho Google Cast), `Windows.Networking`.
- **Miracast:** `MiracastReceiver.dll` của Windows được nạp vào tiến trình. Cổng 7250 do `C:\Windows\System32\CastSrv.exe` giữ, quảng bá `_display._tcp` (Miracast over Infrastructure, MS-MICE). *(suy luận)* AirServer không tự viết Miracast mà gọi API `Windows.Media.Miracast` có sẵn.
- **Capabilities:** `privateNetworkClientServer`, `internetClientServer`, `wifiControl`, `bluetooth`, `Proximity`, `backgroundMediaPlayback`, `startupTask`. Protocol `airserver://`, AppService `com.airserver.appservice`.

## 2. Cổng mạng

| Cổng | Giao thức | Vai trò | Nguồn |
|---|---|---|---|
| UDP 5353 | mDNS | **Tự chạy responder riêng**, không cần Apple Bonjour. Bind theo từng IP adapter | ✅ netstat + manifest |
| TCP 7000 | HTTP/RTSP | AirPlay chính (`_airplay` + `_raop` đều trỏ về 7000) | ✅ |
| TCP 7100 | HTTP | Mirroring kiểu AirPlay 1 (iOS cũ) | ✅ listen, *(suy luận)* vai trò |
| TCP 8008 | HTTP | Google Cast / DIAL setup | ✅ listen |
| TCP 8009 | TLS | Google Cast CastV2 (protobuf over TLS) | ✅ listen + SRV |
| TCP/UDP 49152–65535 | | Luồng dữ liệu động: video mirror, audio RTP, timing, event | manifest |
| UDP 1900 | SSDP/UPnP | DLNA/DIAL discovery | manifest |
| TCP 9999 | | "Logging" (debug) | manifest, không thấy listen |
| 127.0.0.1:60138/60139 | TCP | IPC nội bộ | ✅ |

## 3. Quảng bá mDNS (✅ bắt được khi AirServer khởi động)

AirServer **giả làm Apple TV 4K** (`AppleTV6,2`):

```
Danh._airplay._tcp.local   SRV port 7000
  deviceid=C4:EF:BB:30:E8:68        ← MAC dùng làm ID AirPlay
  features=0x527D5FD1,0x4000A
  flags=0x04
  model=AppleTV6,2   manufacturer=Apple Inc.
  srcvers=375.3      protovers=1.1   vv=2   acl=0
  pk=593e15c6…3f9b99                ← khoá công khai Ed25519 32 byte (pairing)
  pi=6fe70bb2-0e12-7ab9-d779-4ab64a94f9d3

C4EFBB30E868@Danh._raop._tcp.local   SRV port 7000
  am=AppleTV6,2  cn=0,1,2,3  et=0,3  md=2  tp=UDP  vn=65537  vs=375.3
  ft=0x527D5FD1,0x4000A  sf=0x04  da=true  pk=<như trên>

Danh._googlecast._tcp.local   SRV port 8009
  id=6fe70bb20e127ab9d7794ab64a94f9d3   fn=Danh  md=Windows.Desktop  ca=4101  ve=05  st=0  nf=1
```

### Giải mã `features` = `0x4000A527D5FD1`

Tên bit lấy theo tài liệu cộng đồng (openairplay, UxPlay). Apple không công bố.

- **Bật:**
  - Video, VideoHTTPLiveStreams (bit 0, 4): phát video/HLS từ YouTube, Photos…
  - **ScreenMirroring, ScreenRotate** (bit 7, 8)
  - Audio, AudioRedundant (bit 9, 11), AudioFormat1–4 (bit 18–21: PCM/ALAC/AAC/AAC-ELD), RAOP (bit 30)
  - **Authentication4 = FairPlay** (bit 14), FPSAPv2pt5_AES_GCM (bit 12)
  - MetadataArtwork (bit 16), MetadataFeatures3 (bit 50)
  - AirPlayVideoPlayQueue (bit 33), TLS_PSK (bit 35)
  - Bit chưa rõ tên: 6, 10, 22, 25, 28
- **Không bật:**
  - HomeKit pairing (46), CoreUtils pairing (48), SystemPairing (43): dùng pairing kiểu cũ, không phải pairing HomeKit.
  - BufferedAudio (40), PTP (41): **không phải loa AirPlay 2 multi-room**.
  - MFi (26), ScreenMultiCodec (42), AirPlayVideoV2 (49).

Kết luận *(suy luận)*: AirServer khai báo đúng bộ tính năng của một receiver "AirPlay mirroring + audio" giống các dự án mã nguồn mở UxPlay/RPiPlay. Nhưng `srcvers` mới hơn (375.3, so với 220.68 của UxPlay) và có thêm Google Cast.

## 4. Luồng AirPlay mirroring *(suy luận, theo giao thức công khai)*

Chưa bắt traffic thật (xem mục 6). Theo các bản cài đặt công khai, với bộ `features` như trên, phiên mirror sẽ đi như sau:

```
iPhone                                   AirServer :7000
  │ mDNS: thấy _airplay._tcp
  │ GET /info  (binary plist)        ─►  model, features, pk, displays…
  │ POST /pair-setup  (Ed25519 pk)   ─►
  │ POST /pair-verify (Curve25519 ECDH + Ed25519 sign) ─► khoá phiên
  │ POST /fp-setup ×2 (FairPlay SAP) ─►  ← phần độc quyền khó nhất
  │ RTSP SETUP (plist: ekey, eiv, timing port)
  │ RTSP SETUP streams[type=110]     ─►  trả dataPort (TCP động)
  │ RTSP RECORD
  │ ══ TCP dataPort: H.264 (Annex-B/AVCC), mã hoá AES-128-CTR ══►  giải mã → FFmpeg/MF → D3D11
  │ ══ UDP: audio AAC-ELD (RTP), AES-CBC ══►  WASAPI
  │ UDP timing (NTP-like) / POST /feedback mỗi 2 s (keep-alive)
```

- **Khoá AES cho video:** suy ra từ `ekey` (đã mã hoá bằng FairPlay) và shared secret của pair-verify (SHA-512).
- **iOS 17 trở lên** có thể đòi HEVC hoặc pairing mới, tuỳ `features`. AirServer không bật ScreenMultiCodec, nên *(suy luận)* iPhone sẽ gửi H.264.

## 5. Ý nghĩa cho app của bạn

| Câu hỏi | Trả lời |
|---|---|
| iPhone → PC bằng web (Safari) được không? | **Không.** Safari iOS không có `getDisplayMedia`. Chỉ AirPlay (có sẵn trong iOS) hoặc app native dùng ReplayKit mới lấy được màn hình. |
| Tự viết AirPlay receiver như AirServer? | Được, và đây là con đường AirServer chọn. Phần khó là **FairPlay** (`/fp-setup`, giải mã `ekey`). Tham khảo mã nguồn mở **UxPlay** (GPL-3.0, C, chạy được trên Windows) hoặc RPiPlay. Lưu ý: phần FairPlay trong các dự án này là reverse của Apple, cần cân nhắc pháp lý nếu bán thương mại. |
| Có cần Bonjour không? | Không. AirServer tự chạy mDNS responder trên UDP 5353. Bạn có thể dùng thư viện mDNS thuần, ví dụ `mdns`/`bonjour-service` cho Node hoặc `mdns-sd` cho Rust. |
| Đóng gói Store/UWP có vấn đề gì? | Có. App Store bị **loopback isolation**: tiến trình khác trên cùng máy (kể cả trình duyệt) không kết nối vào được. Muốn trang web local nói chuyện với app thì cần build desktop thường (Win32/MSIX full-trust) hoặc dùng `CheckNetIsolation LoopbackExempt`. |
| Kiến trúc gợi ý cho chiều iPhone → PC | PC chạy receiver AirPlay (dựa trên UxPlay), giải mã H.264, rồi có thể đẩy tiếp sang trình duyệt (WebSocket + WebCodecs) dùng chung kênh với chiều PC → iPhone đã làm theo kiểu spacedesk. |

## 6. Chưa xác minh (unknowns)

1. **Nội dung `/info`, pair-setup, fp-setup**: chưa gọi được vì loopback isolation (mục 7).
2. **Traffic mirror thật**: codec (H.264 hay HEVC), độ phân giải, port động, cách mã hoá. Cần mirror iPhone sang AirServer và bắt gói (Wireshark hoặc `pktmon`).
3. Ý nghĩa các bit features 6, 10, 22, 25, 28 và `ca=4101` của Google Cast.
4. Vai trò cụ thể của cổng 7100 và 9999 ở bản này.
5. AirServer dùng phần cứng (MF/D3D11) hay FFmpeg để giải mã video.

## 7. Cách kiểm chứng tiếp (bạn tự chạy, cần quyền admin)

**Gọi endpoint AirServer từ chính máy này:**

```bash
CheckNetIsolation.exe LoopbackExempt -is -n=F3F176BD.AirServer_p8qzvses5c8me
```

Lệnh này mở loopback cho AirServer trong lúc nó còn chạy. Đây là thay đổi cấu hình bảo mật, nên tôi để bạn tự quyết. Sau đó có thể:

```bash
curl -H "User-Agent: AirPlay/690.7.1" http://127.0.0.1:7000/info -o info.plist
```

Một cách khác không cần đổi cấu hình: gọi `/info` từ một máy khác cùng mạng.

**Bắt traffic khi iPhone mirror sang AirServer:**

```bash
pktmon start --capture --pkt-size 0 -f airserver.etl
```

Mirror iPhone khoảng 10 giây, rồi:

```bash
pktmon stop
```

Chuyển sang định dạng Wireshark:

```bash
pktmon etl2pcap airserver.etl -o airserver.pcapng
```
