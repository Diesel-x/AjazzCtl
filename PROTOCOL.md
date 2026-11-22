# AJAZZ AK820 Pro — Reverse-Engineered USB & 2.4 GHz HID Protocol

Данный документ описывает протокол обмена данными между ПК и клавиатурой **AJAZZ AK820 Pro (Tri-Mode, 0.85" TFT RGB)**, восстановленный методом статического анализа (реверс-инжиниринга) из оригинального драйвера `DeviceDriver.exe` и библиотеки `mui.dll` (версия `1.0.0.5`, OEM-ветка `20240103`, сборка `2025-04-07`).

---

## 1. Идентификация устройств и HID-коллекции

Контроллер клавиатуры использует **Vendor ID `0x0C45`** (Sonix / Royuan). Поддерживаются следующие интерфейсы из `config.xml`:

| Режим подключения | VID | PID | Интерфейс (`MI`) | Назначение HID-коллекций |
| :--- | :---: | :---: | :---: | :--- |
| **USB (проводной)** | `0x0C45` | `0x8009` / `0x800A` | `MI_00` | 1. **Control Feature:** `FeatureReportByteLength = 65` (`0x41` байт: `ReportID 0x00` + 64 байта)<br>2. **Screen Bulk Output:** `OutputReportByteLength = 4097` (`0x1001` байт: `ReportID 0x00` + 4096 байт) |
| **2.4 ГГц донгл** | `0x0C45` | `0xFDFD` | `MI_03` | **Vendor HID:** `UsagePage = 0xFF60`, `Usage = 0x0061`<br>`OutputReportByteLength = 33` (`0x21`), `InputReportByteLength = 33` (`0x21`) |
| **Bluetooth** | — | — | GATT `0x1812` / `0x180F` | Конфигурация по Bluetooth в оригинальном ПО отключена |

---

## 2. Транспортный уровень

### 2.1. USB Wired — Feature Control (`0x0044EED0`, `0x00451440`, `0x004514E0`)
* Буфер отчёта: **65 байт** (`raw[0] = 0x00` — Report ID, `raw[1..64]` — полезная нагрузка).
* Отправка: `HidD_SetFeature(hFile, raw, 65)`.
* Задержка после отправки: `35` мс (`cmd_delaytime` из `rgb-keyboard.xml`).
* Если требуется подтверждение (`expectAck == true`):
  * Вызывается `HidD_GetFeature` (или `DeviceIoControl` с `IOCTL_HID_GET_FEATURE = 0x000B0192`, `ack[0] = 0x00`).
  * Ожидается `ack[4] == 0x01` (до 10 попыток с паузой `35` мс).
* Каждая транзакция по USB обрамляется командами начала и конца сессии:
  * **Session Start (`expectAck = true`):** `payload[0..1] = [0x04, 0x18]`, остальные байты `0x00`.
  * **Session End (`expectAck = true`):** `payload[0..1] = [0x04, 0x02]`, остальные байты `0x00`.

### 2.2. USB Wired — Bulk Output (`0x0044F2D0`, `0x00451220`, `0x00451300`)
* Используется для потоковой передачи кадров на TFT-экран.
* Буфер: **4097 байт** (`raw[0] = 0x00` — Report ID, `raw[1..4096]` — 4 КБ блок данных).
* Запись через overlapped `WriteFile(hBulk, raw, 4097)` и ожидание ответа через `ReadFile(hBulk, resp, 4097)` с таймаутом `300` мс.

### 2.3. 2.4 ГГц Wireless — 32-байтовый обмен с контрольной суммой (`0x0044F5F0`)
* Буфер отчёта: **33 байта** (`raw[0] = 0x00` — Report ID, `raw[1..31]` — полезная нагрузка, `raw[32]` — 8-битная контрольная сумма).
* Контрольная сумма (`raw[32]`):
  $$\text{checksum} = \left(\sum_{i=1}^{31} \text{raw}[i]\right) \bmod 256$$
* Запись: `WriteFile(hWireless, raw, 33)`.
* Чтение подтверждения: задержка `5` мс (или `2` мс при пакетной загрузке экрана), затем `ReadFile(hWireless, resp, 33)` с проверкой совпадения первых 3 байт команды ответа (`payload[0..2]`).

---

## 3. Команды протокола

### 3.1. Синхронизация времени (`0x00423940` USB / `0x00423A70` 2.4G)

#### USB-режим (4 Feature-пакета):
1. **Session Start:** `[0x04, 0x18, 0x00, ...]` (`expectAck = true`)
2. **Prepare Time Sync:** `[0x04, 0x28, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, ...]` (`expectAck = true`)
3. **Time Payload (`expectAck = true`):**
   | Индекс `payload` | Индекс `raw` (+1) | Значение |
   | :---: | :---: | :--- |
   | `0` | `1` | `0x00` |
   | `1` | `2` | `slotId` (обычно `0x01`) |
   | `2` | `3` | `0x5A` (маркер команды времени) |
   | `3` | `4` | `Year % 2000` (например, `26` для 2026 года) |
   | `4` | `5` | `Month` (`1..12`) |
   | `5` | `6` | `Day` (`1..31`) |
   | `6` | `7` | `Hour` (`0..23`) |
   | `7` | `8` | `Minute` (`0..59`) |
   | `8` | `9` | `Second` (`0..59`) |
   | `9` | `10` | `0x00` |
   | `10` | `11` | `DayOfWeek` (`0` = Вс .. `6` = Сб) |
   | `62..63` | `63..64` | `0xAA, 0x55` |
4. **Session End:** `[0x04, 0x02, 0x00, ...]` (`expectAck = true`)

#### 2.4 ГГц-режим (1 Output-пакет, 33 байта):
| Байт `raw` | Значение |
| :---: | :--- |
| `0` | `0x00` (Report ID) |
| `1..4` | `0x0C, 0x10, 0x00, 0x00` |
| `5` | `slotId` (`0x01`) |
| `6` | `0x5A` |
| `7..12` | `Year % 2000`, `Month`, `Day`, `Hour`, `Minute`, `Second` |
| `13` | `0x00` |
| `14` | `DayOfWeek` (`0..6`) |
| `15..17` | `0x00` |
| `18..19` | `0xAA, 0x55` |
| `20..31` | `0x00` |
| `32` | 8-битная сумма `raw[1..31]` |

---

### 3.2. Режим RGB-подсветки (`0x0042B100` USB / `0x0042B2A0` 2.4G)

* **Режимы (`mode`):** `0` = Off, `1` = Static, `2` = SingleOn, `3` = SingleOff, `4` = Glittering, `5` = Falling, `6` = Colourful, `7` = Breath, `8` = Spectrum, `9` = Outward, `10` = Scrolling, `11` = Rolling, `12` = Rotating, `13` = Explode, `14` = Launch, `15` = Ripples, `16` = Flowing, `17` = Pulsating, `18` = Tilt, `19` = Shuttle.
* **Параметры:** `R, G, B` (`0..255`), `brightness` (`0..5`), `speed` (`0..5`), `direction` (`0..3`), `colorful` (`0`/`1`).

#### USB-режим:
1. **Session Start:** `[0x04, 0x18, ...]` (`expectAck = true`)
2. **Prepare Light:** `[0x04, 0x13, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, ...]` (`expectAck = true`)
3. **Light Payload (`expectAck = false`):**
   * `payload[0] = mode`, `payload[1..3] = [R, G, B]`, `payload[8..11] = [brightness, speed, direction, colorful]`, `payload[14..15] = [0xAA, 0x55]`
4. **Session End:** `[0x04, 0x02, ...]` (`expectAck = true`)
5. **Apply Light:** `[0x04, 0xF0, ...]` (`expectAck = false`)

#### 2.4 ГГц-режим (33 байта):
* `raw[0] = 0x00`
* `raw[1..3] = [0x05, 0x10, 0x00]`
* `raw[4] = mode`, `raw[5..7] = [R, G, B]`
* `raw[12..15] = [brightness, speed, direction, colorful]`
* `raw[18..19] = [0xAA, 0x55]`
* `raw[32] = sum(raw[1..31]) & 0xFF`

---

### 3.3. Системные настройки (`0x00414060` USB / `0x004142D0` 2.4G)

* **Параметры:** `disableWin` (`0/1`), `disableAltF4` (`0/1`), `disableAltTab` (`0/1`), `responseTimeLevel` (`1..5`), `sleepTime` (`0` = выкл, `1` = 1 мин, `2` = 5 мин, `3` = 30 мин), `fnSwitch` (`0/1`).
* **USB:** `Session Start` -> `[0x04, 0x17, 0x01, 0,0,0,0,0, 0x01]` -> `[0x00, 0x01, disableWin, disableAltF4, disableAltTab, responseTimeLevel, sleepTime, 0x00, fnSwitch, ..., 0xAA@62, 0x55@63]` -> `Session End`.
* **2.4 ГГц:** `raw = [0x00, 0x07, 0x10, 0x00, 0x00, 0x01, disableWin, disableAltF4, disableAltTab, responseTimeLevel, sleepTime, 0x00, fnSwitch, 0..0, 0xAA@18, 0x55@19, 0..0, checksum@32]`.

---

### 3.4. Загрузка изображения / GIF на 0.85" TFT-экран (`0x004229E0` / `0x00423220` / `mui.dll!0x1003C6A0`)

#### Формат буфера кадров (`128 × 128`, RGB565 Little-Endian)
* **Заголовок (`256` байт):**
  * Заполняется значением `0xFF`.
  * `buf[0] = frameCount` (`1..140`).
  * `buf[1 + i] = clamp(delayMs[i] / 2, 1, 255)` для каждого кадра `i`.
* **Пиксельные данные (со смещения `256`, по `32 768` байт на кадр):**
  * Порядок строк: сверху вниз, слева направо (`128 × 128`).
  * Каждый пиксель `(R, G, B)` кодируется в **RGB565 Little-Endian**:
    ```c
    uint16_t rgb565 = ((R & 0xF8) << 8) | ((G & 0xFC) << 3) | (B >> 3);
    buf[off + 0] = (uint8_t)(rgb565 & 0xFF);
    buf[off + 1] = (uint8_t)(rgb565 >> 8);
    ```

#### Передача по USB (`chunkSize = 4096`):
* Буфер дополняется байтами `0xFF` до кратности `4096`. `chunkCount = totalSize / 4096`.
1. **Session Start:** `[0x04, 0x18, ...]` (`expectAck = true`)
2. **Screen Header Feature:** `[0x04, 0x72, slotId, 0,0,0,0,0, chunkCount & 0xFF, chunkCount >> 8, ...]` (`expectAck = true`)
3. Последовательная отправка всех `chunkCount` блоков по 4096 байт через 4097-байтовый Bulk Output pipe (`0x0044F2D0`).
4. **Session End:** `[0x04, 0x02, ...]` (`expectAck = true`)

#### Передача по 2.4 ГГц (`chunkSize = 28`):
> Примечание: в `rgb-keyboard.xml` вкладка экрана для 2.4G скрыта флагом `screen="0"`, но в коде `DeviceDriver.exe` (`0x00423220`) реализован полный 28-байтовый транспорт загрузки кадров по 2.4 ГГц.
* Буфер дополняется байтами `0xFF` до кратности `28`. `packetCount = totalSize / 28`.
1. **Заголовок передачи (33 байта):**
   * `raw[0] = 0x00`, `raw[1..3] = [0x7F, 0x03, 0x00]`, `raw[4] = slotId`, `raw[5..7] = packetCount (24-bit LE)`, `raw[32] = checksum`.
2. **Пакеты данных `p = 0 .. packetCount - 1` (по 33 байта, задержка 2 мс):**
   * `raw[0] = 0x00`
   * `raw[1] = 0x80 | ((p >> 16) & 0x7F)`
   * `raw[2] = p & 0xFF`
   * `raw[3] = (p >> 8) & 0xFF`
   * `raw[4..31] = buf[p * 28 .. p * 28 + 27]`
   * `raw[32] = sum(raw[1..31]) & 0xFF`

---

### 3.5. Поклавишная RGB-подсветка (User Light, `0x0044B900` / `0x0044BA10` USB и `0x0044BE80` 2.4G)

#### Буфер цветов всех клавиш (`576` байт = `0x240` байт)
* Каждая клавиша в `layouts/rgb-keyboard.xml` имеет свой индекс светодиода `light_index` (`1..121`, 81 клавиша).
* Для каждой клавиши по смещению `light_index * 4` записываются 4 байта:
  * `buf[light_index * 4 + 0] = (R == 0 && G == 0 && B == 0) ? 0x00 : light_index`
  * `buf[light_index * 4 + 1] = R`
  * `buf[light_index * 4 + 2] = G`
  * `buf[light_index * 4 + 3] = B`
* В конце буфера ставится маркер `buf[574] = 0xAA`, `buf[575] = 0x55`.

#### Передача по USB (`0x0044B900` + `0x0044BA10`):
1. **Активация пользовательского режима (`mode = 0x80`):**
   * `Session Start`: `[0x04, 0x18, ...]` (`expectAck = true`)
   * `Prepare`: `[0x04, 0x13, 0,0,0,0,0,0, 0x01, ...]` (`expectAck = true`)
   * `Mode`: `payload[0] = 0x80`, `payload[8] = 0x00`, `payload[9] = brightness` (`1..5`), `payload[14] = 0xAA`, `payload[15] = 0x55` (`expectAck = false`)
   * `Session End`: `[0x04, 0x02, ...]` (`expectAck = true`)
   * `Apply`: `[0x04, 0xF0, ...]` (`expectAck = false`)
2. **Запись 576-байтового буфера (`9 × 64` байт):**
   * `Session Start`: `[0x04, 0x18, ...]` (`expectAck = true`)
   * `Prepare Table`: `[0x04, 0x23, 0,0,0,0,0,0, 0x09, ...]` (`expectAck = true`)
   * `9` Feature-пакетов подряд (`buf[i * 64 .. i * 64 + 63]`, `expectAck = false`)
   * `Session End`: `[0x04, 0x02, ...]` (`expectAck = true`)
   * `Apply`: `[0x04, 0xF0, ...]` (`expectAck = true`)

#### Передача по 2.4 ГГц (`0x0044BE80..0x0044C1C0`):
1. **Активация режима `0x80` (33 байта):**
   * `raw[0] = 0x00`, `raw[1..3] = [0x05, 0x10, 0x00]`, `raw[4] = 0x80`, `raw[13] = brightness`, `raw[18..19] = [0xAA, 0x55]`, `raw[32] = checksum`.
2. **Запись буфера в 21 пакете (`blockIndex = 0..20`):**
   * `raw[0] = 0x00`
   * `raw[1] = 0x14`
   * `raw[2] = (blockIndex == 20) ? 0x10 : 0x1C` (длина блока: 28 байт для `0..19`, 16 байт для `20`)
   * `raw[3] = blockIndex`
   * `raw[4 .. 4 + copyLen - 1]` = данные `buf[blockIndex * 28 ..]`
   * `raw[32] = checksum`
   * Пауза `2` мс между пакетами.
