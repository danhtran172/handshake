# spacedesk – Giao thức WebSocket (HTML5 viewer)

Tài liệu reverse để tham khảo khi thiết kế app PC ↔ iPhone qua web.

## Nguồn bằng chứng

| Nguồn | Loại | Ghi chú |
|---|---|---|
| `spacedeskService.exe` (SHA-256 `c9fa7955…8ccff`, build 16/02/2026) | Strings + RTTI (static) | Không có Ghidra/IDA nên **chưa decompile**; chỉ đọc chuỗi log, tên class C++ |
| `spacedesk.min.js` của `viewer.spacedesk.net`, bản Wayback 30/03/2026 | Mã client JS (static, gần như không obfuscate) | Trang gốc hiện trả 404 + chứng chỉ hết hạn → viewer chính thức đã bị gỡ |
| Registry `HKLM\SOFTWARE\datronicsoft\spacedesk` | Cấu hình máy này | |
| `tools/spacedesk-probe.mjs` chạy với service thật (22/10/2026) | **Runtime** | Xác nhận handshake, Identification v4.8, video JPEG, flow control, mouse. Kết quả ở mục 9 |

Mục nào đánh dấu ✅ là đã kiểm chứng bằng runtime. Phần còn lại là đọc tĩnh.

---

## 1. Kiến trúc server (Windows)

```
spacedeskService.exe  (LocalSystem service, server)
├─ L4  Transport   : TCP listener, WebSocket handshake, SSL (OpenSSL), UDP discovery,
│                    USB Android (AOAP), USB iOS (usbmuxd)
├─ L5  Protocol    : Identification, Ping, FlowControl, Settings, Timer/Transmitter/Receiver threads
├─ L6  Frame       : FrameTarget, DirtyRegions
├─ SE  Pipeline    : DXGI Duplication / IddCx → encode (JPEG CPU | H.264 MFT | YUV CPU/GPU | Off)
└─ AL  Drivers     : spacedeskDriverBus, IddCx display, HID keyboard/touch/pen, mouse, audio
```

- ✅ **Cổng TCP: 28252**, listen trên `0.0.0.0` (`netstat`).
- ✅ **UDP discovery cũng là cổng 28252**, bind riêng từng IP adapter (ví dụ `192.168.10.138:28252`).
- **Cùng một cổng nhận cả client native lẫn WebSocket.** `L4SocketTcpCommBase::ReceiveIdentification` đọc vài byte đầu. Nếu là HTTP (`HTTP recognized`) thì trả `HTTP/1.1 101 Switching Protocols` với `Sec-WebSocket-Accept` (SHA-1 qua CryptoAPI, base64). Nếu không, nó coi là header nhị phân spacedesk.
- ✅ Handshake đúng chuẩn RFC 6455, nhưng server **tìm header `Sec-WebSocket-Key` phân biệt hoa/thường**:
  - Header viết thường (`sec-websocket-key`) thì server đóng kết nối, không trả 101.
  - Safari/Chrome gửi đúng kiểu chữ nên không bị.
  - `WebSocket` có sẵn của Node 22 (undici) gửi header viết thường nên **bị từ chối**. Đó là lý do probe phải tự làm WebSocket trên TCP thô.
- Server tự gỡ masking của frame WebSocket do client gửi (`Payload Masked/Unmasked`).
- Có `L4SocketTcpCommSSL` + nạp `.pfx` (`L4WindowsCertificate::KeyPairFileDataGet`) → hỗ trợ TLS/`wss://`. *Chưa rõ*: cổng nào, kích hoạt bằng setting gì.
- `TCP_NODELAY`, QoS flow (`QOSAddSocketToFlow`) cho socket video.
- `L4ThreadNetDiscovery`: UDP broadcast để client native tự tìm server. *Chưa rõ số cổng UDP* (cần disassemble). Web client **không** dùng discovery; người dùng nhập IP.

## 2. Đóng gói chung

- Mỗi gói = **1 WebSocket binary message**.
- **Header cố định 128 byte**, mọi số nguyên **little-endian int32**.

| Offset | Kiểu | Trường |
|---|---|---|
| 0 | u32 | `HeaderType` |
| 4 | u32 | `CountByteDataFollowingHeader` (độ dài payload) |
| 8..127 | | tuỳ loại |

### HeaderType

| Giá trị | Tên | Hướng |
|---|---|---|
| 0 | Identification | Client → Server |
| 1 | Ping | (server; web client không xử lý) |
| 2 | FrameBuffer | Server → Client |
| 3 | Visibility | Server → Client |
| 4 | CursorPosition | Server → Client |
| 5 | CursorBitmap | Server → Client |
| 6 | PowerSuspendHibernate | Server → Client |
| 7 | FlowControlAck | Client → Server |
| 8 | Disconnect | Client → Server |
| 9 | Rotation | ? |
| 10 | Mouse | Client → Server |
| 11 | Keyboard | Client → Server |
| 12 | Touch | Client → Server |

## 3. Bắt tay

1. Client mở `ws://<IP>:28252` với `binaryType = "arraybuffer"`.
2. Ngay khi `onopen`, client gửi **IdentificationPacket = 128 byte header + 334 byte payload = 462 byte**.
3. Nếu không nhận được gói nào trong khoảng timeout thì client báo lỗi. Server không có gói "ACK đăng nhập" riêng.
4. ✅ Thứ tự quan sát được:
   - `Visibility visible=0`.
   - Sau ~2–5 s (khi Windows tạo màn hình ảo) là `Visibility visible=1`.
   - Tiếp theo là các gói `FrameBuffer`.
   - Lần kết nối lại khi màn hình ảo còn tồn tại: nhận `visible=1` gần như ngay lập tức.
5. ✅ Kiểm tra phiên bản:
   - **4.8** và **4.7**: được chấp nhận.
   - **5.0**: server đóng kết nối sau ~2 s, không gửi gì.
   - Suy luận: Major phải khớp, Minor được phép thấp hơn.

### Identification header (type 0)

| Offset | Giá trị (web client) | Ý nghĩa |
|---|---|---|
| 0 | 0 | HeaderType |
| 4 | 334 | độ dài payload |
| 8 | **4** | ProtocolVersion Major |
| 12 | **8** | ProtocolVersion Minor |
| 16 | 1 | ClientType: 0=WindowsRemoteMonitor, 1=WebBrowser, 2=UsbCableApple, 3=KvmWindows |
| 20 | 0 | (OS type?) |
| 24 | 3 | Compression: 0=Off(raw BGRA), 1=H264, 3=JPEG, 4=MJPEGD2 |
| 32 | 1..100 | JPEG quality (mặc định 40) |
| 44 | u16 | FrameRate (0 = không giới hạn) |
| 46 | u16 | 0 |
| 48 | 1 / 2 | 1 = chỉ độ phân giải màn hình, 2 = có thêm custom |
| 52 | w | Width màn hình client |
| 88 | h | Height màn hình client |
| 56 | w2 | Custom width (hoặc 0) |
| 92 | h2 | Custom height (hoặc 0) |
| còn lại | 0 | |

Server kiểm tra (`L5Protocol::PacketIdentificationValidate`): header type, kích thước, **phiên bản giao thức phải khớp**, client type, OS type, compression hợp lệ với client cũ, device identification.

### Identification payload (334 byte)

- Byte 0..19: chuỗi UTF-16LE `"11111" + "22222"` (device ID cố định của web client).
- Từ byte 78: tên thiết bị UTF-16LE, ví dụ `"Safari on iOS"` → hiện trong spacedesk Console.

## 4. Luồng video (Server → Client)

### FrameBuffer header (type 2)

| Offset | Trường |
|---|---|
| 8 / 12 | Width / Height của toàn màn hình ảo |
| 16 | Pitch |
| 20 | Format (ColorType: 0 RGB8 … 4 RGBA32, 5 YUV444, 6 YUV422, 7 YUV420) |
| 24, 28, 32, 36 | Rect cập nhật: X, Y, X2, Y2 |
| 40 | CompressionType |
| 64 | FragmentInfo (1 = là 1 mảnh trong 4 mảnh) |

Payload (sau byte 128):
- `JPEG`/`MJPEGD2`: bytes JPEG (bắt đầu `FF D8`). Client tạo `Blob` → `<img>` → vẽ lên canvas/WebGL tại rect.
- `Off`: raw **BGRA**, kích thước = (X2−X)×(Y2−Y)×4.

**Fragment:** server có thể cắt 1 khung hình thành **4 mảnh** (`FragmentCount` trong log encoder). Client đếm đủ 4 mảnh mới tính là 1 frame.

✅ Quan sát thực tế (1280×720, JPEG q40):
- **Mỗi frame = 4 dải ngang**, mỗi dải cao H/4 (180 px). Mỗi dải là **1 file JPEG độc lập** (`FF D8…`), khoảng 20–25 KB.
- **Thứ tự 4 dải không cố định** (ví dụ 360, 180, 540, 0). Client phải vẽ theo rect trong header, không được giả định thứ tự.
- Rect đôi khi lệch 1–3 px (ví dụ `(0,537)-(1280,719)`) do server căn dirty-region.
- `Format` = 21. Suy luận: `D3DFMT_A8R8G8B8`, tức nguồn là BGRA 32 bit. Không ảnh hưởng khi đã nén JPEG.
- `Pitch` = width × 4.
- Thông lượng: khoảng **25–27 frame/s, khoảng 3,6 Mbit/s** khi nội dung màn hình gần như tĩnh.

### Flow control (rất quan trọng)

- Sau khi vẽ xong **1 frame hoàn chỉnh** (1 gói thường hoặc đủ 4 fragment), client gửi `FlowControlAck` (header 128 byte, type 7, length 0).
- Server chờ ACK trước khi gửi frame kế tiếp (`L5ProtocolFlowControl::WaitForAcknowledge`, cửa sổ `FlowControlTransmitWindowsSize`, timeout `FlowControlTimeoutInMs`). Không gửi ACK thì stream đứng.
- Đây là cơ chế "back-pressure" đơn giản giúp chạy được trên Safari iOS yếu: server không bao giờ đẩy nhanh hơn client vẽ.
- ✅ Đã kiểm chứng: **không gửi ACK** thì server chỉ gửi đúng 1 frame (4 dải), rồi **ngắt kết nối sau khoảng 3 s**. Vậy ACK vừa là flow control vừa là keep-alive.

### Các gói khác

- **Visibility (3):** offset 8 = 1 hiện / 0 ẩn (màn hình ảo bị tắt/khoá).
- **CursorPosition (4):** X@8, Y@12, visible@16.
- **CursorBitmap (5):** W@8, H@12, pitch@16, hotX@20, hotY@24, flags@28 (1 Monochrome, 2 Default, 4 MaskedColor), payload = bitmap.
- Con trỏ được truyền riêng, không vẽ vào frame. Nhờ vậy di chuột không cần encode lại cả màn hình.

## 5. Luồng input (Client → Server)

Tất cả là header 128 byte, length 0.

### Mouse (type 10)

| Offset | Trường |
|---|---|
| 8 / 12 | dX / dY (**tương đối**, đã nhân tỉ lệ desktop/canvas) |
| 16 | ButtonData (wheel delta) |
| 20 | ButtonFlags: Move 0x1, LDown 0x2, LUp 0x4, RDown 0x8, RUp 0x10, MDown 0x20, MUp 0x40, XDown 0x80, XUp 0x100, Wheel 0x800, HWheel 0x1000, VirtualDesk 0x4000, Absolute 0x8000 |
| 24 | Flags = 0 |

Các cờ này trùng với `MOUSEEVENTF_*` của Win32 `SendInput`.

✅ Đã kiểm chứng: gửi `Move dx=120 dy=60` thì con trỏ Windows dịch đúng (+120, +60) pixel, trong vòng vài chục ms.

### Keyboard (type 11)

| Offset | Trường |
|---|---|
| 8 | VirtualKey (`event.keyCode`) |
| 12 | ScanCode (web client gán = keyCode) |
| 16 | Flags: ExtendedKey 0x1, KeyUp 0x2, Unicode 0x4, ScanCode 0x8 |

Các cờ này trùng với `KEYEVENTF_*` của Win32.

### Touch (type 12)

| Offset | Trường |
|---|---|
| 8 / 12 | X / Y theo pixel của canvas client (`clientX/clientY`) |
| 16 / 20 | ResX / ResY = kích thước canvas (server tự scale sang desktop) |
| 24 | Flags: 0x1 absolute, 0x2 down, 0x4 up (không cờ = move) |
| 28 | timestamp ms |

Server bơm touch qua driver HID ảo (`IspsdkDeviceHidTouch`). Đó là multi-touch thật của Windows, không phải giả lập chuột.

### Disconnect (type 8)

Client gửi 28 byte (`headerKVMLength`) với type = 8 rồi `close(1000)`.

## 6. Cấu hình server (registry)

`HKLM\SOFTWARE\datronicsoft\spacedesk\...`:
- `FlowControlTimeoutInMs`, `FlowControlTransmitWindowsSize`, `InactivityDisconnectTimeoutMs`, `PingEchoRequestIntervalMs`, `PingEchoReplyIntervalMs`
- `CompressionQuality`, `JpegHuffOptimized`, `ForceH264SwEncoding`
- Mỗi client lưu 1 key GUID: `Width/Height/FramesPerSec/EncodingType/OsType/CommunicationProtocol/ClientDescription`
- `Service\EncryptionPassword` có tồn tại. *Chưa rõ* nó dùng vào việc gì: mã hoá giao thức cho client mới, hay chỉ cho USB/licensing.

## 7. Những điểm chưa xác minh (unknowns)

1. ~~Phiên bản giao thức~~: ✅ đã xác nhận 4.8 hoạt động với service build 02/2026.
2. **Ping (type 1).** Trong ~10 s test, server không gửi gói type 1 nào cho WebBrowser client. Có vẻ ACK đóng vai keep-alive. Chưa thử phiên dài hơn.
3. **Định dạng gói UDP discovery** (cổng đã biết là 28252) và **cổng/điều kiện bật TLS**.
4. **Keyboard, Touch** chưa test runtime (chỉ Mouse đã test).
5. **Ý nghĩa offset 20 (OsType), 9 (Rotation), 6 (Power)** và payload của chúng.
6. **EncodingType = 5** thấy trong registry (client Android). Giá trị này không có trong enum của JS, có thể là codec mới (HEVC?).

## 8. Bài học cho app PC ↔ iPhone qua web

| spacedesk làm | Gợi ý cho app của bạn |
|---|---|
| Server chạy trên PC, cổng 28252, trang web host ở domain riêng | Cho PC tự phục vụ luôn trang HTML/JS (HTTP + WS cùng cổng). Không cần host ngoài, tránh lỗi mixed-content `https` → `ws://` |
| JPEG từng frame + ACK 1 frame/lần | Cách dễ nhất chạy ổn trên Safari iOS. Bắt đầu bằng cách này |
| Dirty rect + 4 fragment | Chỉ gửi vùng thay đổi để giảm băng thông |
| Con trỏ gửi riêng | Nên làm, giảm lag khi di chuột |
| Input = struct nhị phân map thẳng `SendInput` | Tương tự. Touch nên dùng absolute + kích thước canvas để server tự scale |
| H.264 cho client native | Trên iOS web có thể dùng WebCodecs `VideoDecoder` (Safari 16.4+) hoặc WebRTC. Nâng cấp sau khi bản JPEG chạy |
| Màn hình ảo bằng driver IddCx | Chỉ cần nếu muốn iPhone làm **màn hình phụ**. Nếu chỉ mirror/điều khiển thì DXGI Desktop Duplication là đủ |

> Lưu ý pháp lý: dùng tài liệu này để hiểu cách hoạt động và tự thiết kế giao thức riêng. Không nên sao chép mã của spacedesk, và không phát hành client tương thích nếu chưa đọc kỹ EULA của họ.

## 9. Kiểm chứng runtime – `tools/spacedesk-probe.mjs`

Yêu cầu: `spacedeskService` đang chạy (`sc start spacedeskService`, cần quyền admin).

```bash
node tools/spacedesk-probe.mjs --seconds 10 --save ./frames
```

| Lệnh | Kết quả 22/10/2026 |
|---|---|
| mặc định (v4.8, JPEG q40, 1280×720) | 101 OK → Visibility 0 → Visibility 1 → 220 FrameBuffer / 55 frame / ACK 55, khoảng 3,6 Mbit/s |
| `--noAck` | 1 frame (4 dải) rồi server ngắt sau khoảng 3 s |
| `--minor 7` | Được chấp nhận, stream bình thường |
| `--major 5 --minor 0` | Server đóng kết nối sau khoảng 2 s, 0 gói |
| `--mouseDx 120 --mouseDy 60` | Con trỏ Windows dịch đúng +120/+60 |
| Node `WebSocket` có sẵn (undici) | Bị từ chối do header viết thường. Dùng client TCP thô trong probe |
