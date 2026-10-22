# Handshake – PC ⇄ iPhone

Bản PC (Windows, Electron), kèm web client cho iPhone (Safari) và mockup app iOS native.

| Tính năng | Trạng thái bản này |
|---|---|
| **iPhone làm màn hình phụ** (PC → iPhone, chạm để điều khiển) | ✅ Chạy: WebRTC H.264, tự chuyển sang JPEG nếu cần, cảm ứng → chuột Windows |
| **Phát màn hình iPhone lên PC** (iPhone → PC) | ✅ Phía PC nhận/hiển thị/chụp/quay xong. iPhone qua web chỉ phát được **màn hình demo** (Safari không cho quay màn hình). Màn hình thật cần app iOS dùng ReplayKit, xem mockup |
| Giao diện "giấy" giống nhau hai bên | ✅ Một design system `src/shared/paper.css` cho PC, web iPhone và mockup |
| Đồng bộ hai bên | ✅ State chung: cài đặt, phiên, thống kê. Đổi ở đâu cũng cập nhật ngay ở bên kia. Cài đặt và mã ghép nối được lưu lại |

## Chạy

```bash
npm install
```

```bash
npm start
```

- Trên PC: trang **Tổng quan** có mã QR.
- Trên iPhone (cùng Wi‑Fi): mở Camera, quét QR, mở bằng Safari.
  - Nên chọn Chia sẻ → **Thêm vào MH chính** để chạy toàn màn hình như một app.
  - Mã ghép nối được lưu, nên lần sau iPhone tự kết nối lại.
- Thử không cần iPhone: bấm **Xem thử phía iPhone** ở góc dưới bên trái, app mở một cửa sổ giả lập.
- Xem mockup app iOS: bấm **Mockup app iOS**, hoặc mở `http://<ip-pc>:7420/mockup`.
- Lần đầu chạy, Windows Firewall sẽ hỏi quyền mạng: chọn **Private network**.

Tạo shortcut **Handshake** trên Desktop và Start Menu (icon riêng, mở thẳng app, không hiện cửa sổ console):

```bash
npm run shortcut
```

Nếu đổi `src/ios/icon.svg`, chạy `npm run icon` để tạo lại `assets/icon.ico` rồi tạo lại shortcut. Nếu chuyển thư mục project thì cũng chạy lại `npm run shortcut`.

Tuỳ chọn khi chạy:

- `npm run dev`: mở DevTools.
- `npx electron . --verbose`: in log renderer, bật `GET /debug/snapshot?page=home|display|mirror|settings` (chỉ localhost) để chụp cửa sổ.
- `npm run snapshot`: chụp trang Tổng quan ra `captures/ui-pc.png` rồi thoát.

## Hai tính năng hoạt động thế nào

```
                        ┌──────────── PC (Electron) ─────────────┐
 iPhone Safari          │ main: HTTP + WebSocket hub :7420        │
 ───────────────        │   - phục vụ src/ios + src/shared        │
 ws /ws  ◄──────────────┤   - state chung (settings/session/...)  │
   state, signal        │   - chuyển tiếp tín hiệu WebRTC          │
   input (chạm)  ──────►│   - input → SendInput (PowerShell)      │
                        │ renderer (cửa sổ PC):                   │
 WebRTC media  ◄═══════►│   - getDisplayMedia → WebRTC/JPEG       │
                        │   - nhận WebRTC từ iPhone → <video>     │
                        └─────────────────────────────────────────┘
```

**Màn hình phụ (`session.mode = "display"`)**
1. Một trong hai bên gửi `session.start`. Server phát state mới cho cả hai.
2. Cửa sổ PC chụp màn hình đã chọn (`setDisplayMediaRequestHandler` chọn đúng màn hình, không hiện hộp chọn), rồi tạo **offer WebRTC** gửi sang iPhone. Ưu tiên H.264 vì iPhone giải mã bằng phần cứng.
3. Nếu sau 8 giây chưa kết nối, hoặc iPhone không có WebRTC, PC chuyển sang **JPEG qua WebSocket**, mỗi lần chỉ một khung đang bay (kiểu ACK của spacedesk).
4. Chạm trên iPhone gửi `{ t: "input", action, x, y }` với toạ độ chuẩn hoá 0..1. Main đổi sang pixel vật lý của màn hình đích rồi bơm chuột qua `SetCursorPos`/`mouse_event`.
   - 1 ngón chạm = click.
   - Giữ rồi kéo = kéo thả.
   - 2 ngón = cuộn.
   - 2 ngón chạm = chuột phải.
   - Chế độ **Bàn di**: điều khiển con trỏ tương đối như trackpad.

**Phát lên PC (`session.mode = "mirror"`)**
- iPhone tạo offer WebRTC, PC trả answer và hiện video trong khung điện thoại. Khung tự theo tỉ lệ và hướng của video.
- PC có các nút: Xoay, Chụp màn hình (lưu vào `Ảnh\Handshake`), Quay video (`.webm`), Toàn màn hình.
- Bản web phát một canvas demo (`canvas.captureStream`). App iOS sẽ thay canvas bằng **ReplayKit Broadcast Upload Extension**, giữ nguyên phía PC.

## Giới hạn hiện tại

- **Màn hình mở rộng thật** cần driver màn hình ảo trên Windows, ví dụ *Virtual Display Driver* (mã nguồn mở). Khi đó màn hình mới hiện trong sơ đồ ở trang Màn hình phụ. Không có driver thì iPhone hiển thị bản sao một màn hình đang có.
- **Máy khoá màn hình thì Windows trả khung đen** cho mọi cách chụp. Đây là hành vi của hệ điều hành.
- Safari chỉ cho WebRTC/WebSocket không mã hoá (`http`/`ws`) trong LAN. Các API cần HTTPS (clipboard, camera) chưa dùng.
- Chưa có xác thực ngoài mã 8 ký tự trong QR. Chỉ nên dùng trong mạng tin cậy.

## Cấu trúc

```
src/
  main/      main.js (Electron), server.js (HTTP+WS hub), input.js (SendInput), preload.js
  renderer/  cửa sổ PC: index.html, app.js, pc.css
  ios/       web client iPhone: index.html, ios.js, ios.css, manifest; mockup.html
  shared/    paper.css (design system), ui.js (cài đặt dùng chung), state.js, client.js, rtc.js
docs/        spacedesk-protocol.md, airserver-analysis.md (tài liệu reverse)
tools/       spacedesk-probe.mjs, airplay-pcap-analyze.py
captures/    ảnh chụp giao diện
```

### Message WebSocket (`/ws`, JSON)

| Hướng | Message |
|---|---|
| client → server | `hello {role:"pc"\|"ios", token, name, model, caps}`, `set {path:"settings.x", value}`, `session.start {mode, deviceId?, displayId?}`, `session.stop`, `session.status {status, transport, error}`, `display.select {displayId}`, `stats {...}`, `signal {to, data}`, `input {...}`, `ack`, `rename {name}` |
| server → client | `welcome {id, state}`, `state {state}`, `signal {from, data}`, `ack`, `error {message}` |
| binary | khung JPEG: PC → server → iPhone của phiên |

## Engine native (120 fps)

`engine/` là engine stream viết bằng C# (.NET 10): chụp màn hình bằng GPU, nén phần cứng chế độ độ trễ thấp, chạy nhiều đường truyền và tự chuyển đường khi một đường chết. Có kèm client test trên Windows để đo fps và độ trễ thật. Trang **Hiệu năng** trong app chạy đo bằng một nút bấm.

```bash
npm run engine:build
```

```bash
npm run engine:bench
```

Kết quả, kiến trúc và giao thức nằm trong [engine/README.md](engine/README.md). Tóm tắt:
- Đạt 118–119 fps ở 2556×1179, độ trễ p50 khoảng 7 ms khi GPU AMD chạy đủ xung.
- Rút cáp giữa chừng thì hình chỉ khựng khoảng 30–40 ms.
- Muốn 120 fps *ổn định* cần NVENC trên RTX 4060 (bước tiếp theo).

## App iOS native (build không cần Mac)

`ios-app/` là khung app Swift: app chính làm màn hình phụ, cùng một Broadcast Upload Extension phát màn hình iPhone lên PC.
- Project mô tả bằng XcodeGen.
- **GitHub Actions** (`.github/workflows/ios.yml`) build ra `.ipa` chưa ký.
- Ký và cài từ Windows bằng **Sideloadly**.

Hướng dẫn và trạng thái chi tiết xem ở [ios-app/README.md](ios-app/README.md).

Phía PC nhận màn hình iPhone:

```bash
npm run engine:mirror
```

## Bước tiếp theo

1. App iOS native:
   - Build lần đầu trên GitHub Actions.
   - Đo trên iPhone thật.
   - Thêm âm thanh và xoay hình cho luồng phát lên PC.
   - Thêm Live Activity khi đang phát.
2. Tích hợp và tự cài driver màn hình ảo, đặt độ phân giải đúng tỉ lệ iPhone.
3. Đóng gói Windows bằng installer Win32 thường, không đóng gói Store/UWP. App Store bị loopback isolation (xem `docs/airserver-analysis.md`).
