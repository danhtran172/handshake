# Handshake cho iOS (app native)

App native cho iPhone, gồm hai phần:

| Phần | Việc | Nối với |
|---|---|---|
| App **Handshake** | iPhone làm **màn hình phụ**: hiện màn hình PC, chạm để điều khiển chuột | `hs-engine host` (cổng 7430, Bonjour `_handshake._tcp`) |
| Extension **HandshakeBroadcast** (ReplayKit) | **Phát màn hình iPhone lên PC** | `hs-engine mirror` (cổng 7432, Bonjour `_hs-mirror._tcp`) |

Không cần Mac: code viết trên Windows, **GitHub Actions** build trên máy macOS ra file `.ipa` chưa ký, **Sideloadly** ký và cài từ Windows.

## Cấu trúc

```
ios-app/
  project.yml              XcodeGen tạo Handshake.xcodeproj từ file này (không commit .xcodeproj)
  Shared/                  dùng chung cho app và extension
    Wire.swift             giao thức Handshake v1 (giống engine/HsEngine/Net/Protocol.cs), đồng bộ đồng hồ
    MessageChannel.swift   khung message trên NWConnection (TCP, no-delay), đếm khung video đang chờ gửi
    AnnexB.swift           Annex-B ⇄ AVCC/HVCC, tạo CMVideoFormatDescription từ SPS/PPS(/VPS)
  Handshake/               app chính (SwiftUI)
    HandshakeApp.swift     màn hình chính kiểu "giấy", trình xem toàn màn hình, nút phát ReplayKit
    Discovery.swift        tìm PC qua Bonjour
    StreamSession.swift    nhận video, giải mã phần cứng (AVSampleBufferDisplayLayer), ping 100 ms, tự nối lại
    VideoView.swift        lớp hiển thị và chạm → chuột PC
    Paper.swift            màu và kiểu chữ lấy từ src/shared/paper.css
  HandshakeBroadcast/      Broadcast Upload Extension
    SampleHandler.swift    tìm PC, gửi Hello/Config/Video, trả lời ping, nhận KeyRequest
    VideoEncoder.swift     VideoToolbox H.264: low-latency rate control, real time, không B-frame
```

## Build ra IPA (không cần Mac)

1. Đưa project lên một repo GitHub. Workflow nằm ở `.github/workflows/ios.yml`.
   - Workflow tự chạy khi có thay đổi trong `ios-app/`, hoặc bấm **Run workflow** ở tab Actions.
   - Repo public thì build miễn phí. Repo private bị trừ phút build, và phút macOS tính đắt hơn nhiều.
2. Workflow sẽ:
   - cài XcodeGen và chạy `xcodegen generate`;
   - chạy `xcodebuild` cho `iphoneos` với `CODE_SIGNING_ALLOWED=NO`;
   - nén `Payload/Handshake.app` thành `Handshake-unsigned.ipa`.
3. Tải artifact **Handshake-unsigned-ipa** ở trang của lần chạy workflow. Nếu build lỗi, artifact **build-log** chứa log đầy đủ.

## Cài lên iPhone từ Windows

1. Cài **Apple Devices** từ Microsoft Store (hoặc iTunes) để Windows có driver iPhone.
2. Cài **Sideloadly**, cắm iPhone bằng cáp, chọn **Tin cậy** trên iPhone.
3. Kéo `Handshake-unsigned.ipa` vào Sideloadly, nhập Apple ID, rồi bấm Start. Sideloadly ký cả app lẫn extension.
4. Trên iPhone:
   - Cài đặt → Cài đặt chung → **Quản lý VPN & thiết bị** → tin cậy Apple ID của bạn.
   - Cài đặt → Quyền riêng tư & Bảo mật → bật **Chế độ nhà phát triển** rồi khởi động lại máy (iOS 16 trở lên).
5. Lần đầu mở app, chọn **Cho phép** khi iOS hỏi quyền mạng cục bộ. Thiếu quyền này app không tìm thấy PC.

Giới hạn khi dùng Apple ID miễn phí:
- App hết hạn sau **7 ngày**. Sideloadly tự ký lại được khi PC bật và iPhone cùng mạng.
- Cài được tối đa 3 app, và khoảng 10 App ID mỗi tuần; app này dùng 2 (app chính + extension).
- Không dùng **App Groups**, nên extension tự tìm PC bằng Bonjour chứ không đọc địa chỉ từ app.

## Dùng

**Màn hình phụ (PC → iPhone)**
1. Trên PC, mở trang **Hiệu năng** trong app Handshake và bật engine host, hoặc chạy:
   ```bash
   hs-engine host --source desktop --fps 120
   ```
2. Trên iPhone, PC hiện trong danh sách; chạm vào để kết nối. Nếu không thấy, nhập IP của PC.
3. Chạm, giữ rồi kéo trên hình sẽ thành click và kéo chuột trên PC.
4. Dòng chữ góc trên hiện số liệu: fps, độ trễ từ lúc PC chụp tới lúc iPhone xếp khung vào lớp hiển thị, Mbit/s và RTT.

**Phát màn hình lên PC (iPhone → PC)**
1. Trên PC chạy:
   ```bash
   npm run engine:mirror
   ```
2. Trong app, bấm nút tròn vàng rồi chọn **Bắt đầu phát**. Extension tự tìm PC và cửa sổ hình hiện trên PC.
3. Nếu không tìm thấy PC sau 20 giây, iOS dừng phát và báo lỗi.

**Cáp USB:** bật **Điểm truy cập cá nhân** trên iPhone rồi cắm cáp vào PC. Windows có thêm một card mạng (172.20.10.x), và PC hiện trong danh sách như khi dùng Wi‑Fi. Cần gói cước cho phép bật hotspot.

## Đã kiểm và chưa kiểm

- ✅ **Phía PC của luồng phát lên PC:** `hs-engine mirror` đã chạy thử đầu cuối, với một script Python đóng vai extension gửi đúng các message như trên.
  - PC nhận Hello và Config.
  - Giải mã bằng DXVA trên RTX 4060, 0 lỗi.
  - Ping và đồng bộ đồng hồ hoạt động (RTT 0,2 ms).
- ✅ **Bonjour trên Windows:** `_handshake._tcp` và `_hs-mirror._tcp` được bộ mDNS của Windows trả lời (đã truy vấn thử).
- ⚠️ **Mã Swift chưa được biên dịch:** máy này không có trình biên dịch Swift. Lần build đầu trên GitHub Actions là lần kiểm đầu tiên; nếu có lỗi, sửa theo artifact `build-log`.
- ⚠️ **Chưa chạy trên iPhone thật**, nên chưa đo được fps và độ trễ thật.

## Giới hạn hiện tại và việc tiếp theo

- **Phát lên PC:** ReplayKit chỉ cấp tối đa khoảng 60 khung/giây. Hình luôn ở chiều dọc (chưa xoay theo `RPVideoSampleOrientationKey`), và chưa có âm thanh.
- **Màn hình phụ:**
  - Mới có một đường kết nối, tự nối lại khi rớt.
  - Chưa có nhiều đường song song (cáp + Wi‑Fi) như `PathManager` trên PC.
  - Hiển thị qua AVSampleBufferDisplayLayer. Muốn chắc chắn 120 Hz cần chuyển sang VTDecompressionSession + `CAMetalLayer`/`CADisplayLink` với `preferredFrameRateRange`.
- **Cử chỉ:** mới có một ngón (click và kéo). Còn thiếu cuộn bằng 2 ngón, chuột phải và chế độ bàn di như bản web.
- **Kết nối:** chưa có usbmux (cáp không cần hotspot).
