# Handshake engine (native, 120 fps)

Engine stream viết bằng C# (.NET 10) và [Vortice](https://github.com/amerkoleci/Vortice.Windows) (D3D11, DXGI, Media Foundation). Không cần cài SDK C++ hay FFmpeg. Nó thay cho đường WebRTC/JPEG của bản web khi cần **120 fps, độ trễ thấp, không gián đoạn**. App iOS native sau này sẽ nói chuyện với engine bằng giao thức ở dưới; trong lúc chưa có app iOS, `hs-engine client` đóng vai iPhone.

```bash
npm run engine:build
```

```bash
npm run engine:bench
```

Trong app PC còn có trang **Hiệu năng**: chạy đo bằng một nút bấm và bật/tắt engine host cho app iOS.

## Kiến trúc

```
host (PC)                                                    client (iPhone / test client)
─────────                                                    ─────────────────────────────
nguồn hình ── GPU ──► VideoProcessor ──► encoder phần cứng ──► đường chính ──► decoder DXVA ──► GPU vẽ
 · DXGI Desktop Duplication   BGRA→NV12 (+scale)   AMF/QSV qua MF       (TCP)        ▲                  │
 · hình test 120 Hz (GPU)     BT.709 limited       low-latency, CBR      đường dự phòng ┘   đo độ trễ ◄──┘
                                                   không B-frame, VBV 1.5 khung       ping 100 ms / đường
```

- **Không copy qua CPU**: chụp, đổi màu, mã hoá đều trên cùng một GPU, texture đi thẳng vào encoder.
- **Không để độ trễ dồn lại:**
  - Encoder còn bận thì bỏ lần chụp đó (`busyDrops`).
  - Đường truyền có quá 2 khung chờ gửi thì bỏ lần chụp kế tiếp (`congestionDrops`).
  - Client tụt quá 3 khung thì nhảy tới keyframe kế tiếp.
- **Không gián đoạn** (`PathManager` + phiên ở host):
  - Client mở 1 đường cho mỗi mạng (ví dụ cáp trước, Wi‑Fi sau), tất cả gắn vào cùng một phiên.
  - Mỗi đường được ping mỗi 100 ms; đường nào im lặng quá 400 ms coi như chết.
  - Video chuyển ngay sang đường còn sống và phát keyframe. Host cũng tự chuyển, không cần chờ client báo.
  - Encoder vẫn chạy trong lúc chuyển, và giữ phiên 10 giây khi không còn đường nào.
  - Đường đã chết tự nối lại; khi đường ưu tiên (cáp) sống lại thì video quay về đường đó.
- **Đồng hồ:**
  - Mỗi khung mang timestamp QPC lúc chụp.
  - Cùng máy thì độ trễ chính xác tới µs, vì QPC dùng chung cho cả hệ thống.
  - Khác máy thì bù lệch đồng hồ bằng ping (lấy mẫu có RTT nhỏ nhất).
- **Input:** client gửi chạm/chuột theo toạ độ chuẩn hoá. Host bơm vào Windows theo pixel vật lý (engine là PerMonitorV2 DPI-aware).
- **Tìm PC (Bonjour):**
  - Host quảng bá `_handshake._tcp`, còn `mirror` quảng bá `_hs-mirror._tcp`.
  - Dùng bộ mDNS có sẵn của Windows (`DnsServiceRegister`), nên app iOS tìm thấy PC mà không cần gõ IP.
- **Chiều ngược lại (iPhone → PC):** `hs-engine mirror` mở cổng 7432 và đợi extension ReplayKit của app iOS kết nối tới. Trên kết nối này iPhone đóng vai host (gửi Hello, Config, Video), còn PC ping và gửi KeyRequest. Phần giải mã và hiển thị dùng chung với client test (`IVideoLink` → `MirrorLink`).

### Giao thức (`Net/Protocol.cs`)

Mỗi message: `[u32 len][u8 type][payload]`, little-endian.

| Type | Hướng | Nội dung |
|---|---|---|
| 1 Hello | client→host | version, maxW, maxH, fps, codecs, **sessionId** (0 = phiên mới, ≠0 = gắn thêm đường), tên |
| 2 Config | host→client | codec, w, h, fps, kbps, sessionId |
| 3 Video | host→client | header 32 byte (frameId, captureUs, sendUs, encodeUs, cờ keyframe) + access unit Annex‑B |
| 4/5 Ping/Pong | hai chiều | đo RTT và lệch đồng hồ cho từng đường |
| 6 KeyRequest | client→host | xin IDR |
| 7 Input | client→host | loại, nút, x/y 0..65535, wheel |
| 10 Use | client→host | dùng đường này để chạy video |

Ở chế độ `mirror` thì đổi vai: iPhone gửi Hello, Config và Video; PC gửi Ping và KeyRequest; cả hai bên đều trả lời Ping bằng Pong.

## Kết quả đo (22/10/2026, laptop Ryzen 7 8745H + Radeon 780M + RTX 4060)

Host mã hoá trên **AMD 780M** (AMF qua Media Foundation). Client test giải mã trên **RTX 4060** để đóng vai iPhone (không tranh GPU với host). Đo bằng `hs-engine bench`, mỗi lần 8 giây, qua TCP trên máy.

| Cấu hình | fps | Độ trễ chụp→GPU vẽ xong (p50 / p95 / p99) | Mã hoá | Ghi chú |
|---|---|---|---|---|
| Hình test 1920×1080@120, H.264 40 Mbit/s | **117–119** lúc tốt nhất | 7,0–9,3 / 9,0–11,2 / 9,3–20 ms | 6,5 ms | 0 khung bỏ, 0 lỗi |
| Hình test 2556×1179@120 (iPhone Pro), H.264 50 Mbit/s | **118–119** lúc tốt nhất | 7,0–7,7 / 9,0–9,5 / 9,5–21 ms | 7,0 ms | |
| Cùng các cấu hình trên khi GPU AMD hạ xung | **62–95** | 10–15 / 21–33 / 25–37 ms | 10–16 ms | encoder trễ hơn 8,3 ms → bỏ khung |
| Như dòng 2 + **rút cáp ở giây thứ 3** | không đổi | khoảng hở lớn nhất lúc chuyển đường **28–39 ms** | | 0 lỗi giải mã, tự quay về đường chính |
| Màn hình thật 144 Hz (DXGI) + cửa sổ chuyển động | 35–64 | 13–20 ms p50 | 13–26 ms | iGPU vừa vẽ, vừa compose, vừa mã hoá |

Băng thông mạng (loopback) chỉ khoảng 0,1 ms; giải mã khoảng 0,3–0,5 ms. **Gần như toàn bộ độ trễ nằm ở bộ mã hoá.**

### Kết luận

1. **Đường truyền và cơ chế không gián đoạn đã đạt:**
   - Mạng không phải nút thắt.
   - Rút "cáp" giữa chừng thì hình chỉ khựng khoảng 30–40 ms (3–4 khung), không lỗi.
2. **120 fps đạt được nhưng chưa ổn định trên iGPU AMD.** Khối mã hoá VCN của 780M mất khoảng 7 ms mỗi khung ở độ phân giải iPhone, sát ngân sách 8,3 ms, và chậm hẳn khi GPU hạ xung hoặc đang bận vẽ.
3. **Muốn 120 fps ổn định thì cần NVENC trên RTX 4060.** Media Foundation không khởi tạo được encoder NVIDIA trên máy Optimus này (`E_UNEXPECTED` ở `ActivateObject`, kể cả khi liệt kê theo LUID), nên phải gọi **NVENC SDK** trực tiếp (`nvEncodeAPI64.dll` có sẵn trong driver).

## Việc tiếp theo

| Ưu tiên | Việc | Lý do |
|---|---|---|
| 1 | NVENC trực tiếp (NVENC SDK qua P/Invoke) | 120 fps ổn định, mã hoá khoảng 2–3 ms, không đụng iGPU |
| 2 | Màn hình ảo **SudoMaker VDD** (đã cài trên máy) gắn với GPU NVIDIA, độ phân giải và tần số = iPhone | iPhone là màn hình mở rộng thật; chụp và mã hoá cùng trên RTX 4060 |
| 3 | Đường **USB qua usbmux** (Apple Mobile Device Service) | "Cáp" thật cho iPhone; giao thức TCP hiện tại dùng nguyên được |
| 4 | Đường **UDP + FEC** cho Wi‑Fi | Tránh nghẽn chờ TCP gửi lại gói khi Wi‑Fi rớt gói |
| 5 | App iOS: đã có khung trong `ios-app/` (xem [ios-app/README.md](../ios-app/README.md)); còn đo trên iPhone thật và chuyển sang `CAMetalLayer` 120 Hz | Client thật thay cho test client |

## Lệnh

```bash
hs-engine devices
```

```bash
hs-engine host --source desktop --fps 120 --kbps 40000
```

```bash
hs-engine client --host 127.0.0.1,192.168.1.10 --input
```

```bash
hs-engine mirror --port 7432
```

```bash
hs-engine bench --seconds 8 --headless --gpu amd --client-gpu nvidia --size 2556x1179 --kbps 50000
```

```bash
hs-engine bench --source desktop --client-gpu nvidia
```

```bash
hs-engine bench --headless --host 127.0.0.1,192.168.1.10 --drop-primary-at 3
```

Cách dùng từng lệnh:
- `devices`: liệt kê màn hình và bộ mã hoá phần cứng.
- `host`: engine host (Electron bật qua trang Hiệu năng).
- `client`: client test có cửa sổ, nhiều đường, chuột được chuyển về host khi có `--input`.
- `mirror`: cửa sổ trên PC hiện màn hình iPhone do app iOS phát lên (`npm run engine:mirror`).
- `bench`: đo; bản `--source desktop` dùng màn hình thật kèm cửa sổ chuyển động, bản `--drop-primary-at` thử rút cáp ở giây thứ 3.

Host in mỗi sự kiện thành một dòng JSON (`listening`, `stream`, `host-stats`, `path-attached`, `path-lost`…); app Electron đọc các dòng này.
