#include <Arduino.h>
#include <esp_mac.h>
#include <esp_ota_ops.h>

constexpr char FIRMWARE_VERSION[] = "0.1.0";
constexpr char HARDWARE_ID[] = "VOLTRA-S3-ZERO";
constexpr int PROTOCOL_VERSION = 1;

#if !defined(CONFIG_IDF_TARGET_ESP32S3)
#error "Select an ESP32-S3 board. These five potentiometer pins are for the S3."
#endif

// For a four-pin OLED, keep OLED_I2C_SETUP and select the driver over Serial.
#define OLED_OFF 0
#define OLED_SSD1309_I2C 1
#define OLED_SSD1309_SPI 2
#define OLED_SSD1306_I2C 3
#define OLED_SH1106_I2C 4
#define OLED_I2C_SETUP 5
#define OLED_MODE OLED_I2C_SETUP

// ============================================
// VOLTRA — HARDWARE CONFIGURATION
// Waveshare ESP32-S3-Zero
// ============================================

// Potentiometers (left to right)
constexpr int POT_PINS[5] = {
    11, 7, 8, 9, 10
};
constexpr uint8_t CHANNEL_COUNT = sizeof(POT_PINS) / sizeof(POT_PINS[0]);

// OLED I2C
constexpr int OLED_SCL_PIN = 12;
constexpr int OLED_SDA_PIN = 13;
constexpr uint8_t OLED_ADDRESS = 0x3C;  // Some I2C modules use 0x3D.

#if OLED_MODE == OLED_SSD1309_SPI
// Optional SPI wiring from the original sketch. Remap any pins shared with
// POT_PINS before selecting this mode on the new board.
constexpr int OLED_SPI_SCK_PIN = 9;
constexpr int OLED_SPI_MOSI_PIN = 8;
constexpr int OLED_SPI_CS_PIN = 10;
constexpr int OLED_SPI_DC_PIN = 11;
constexpr int OLED_SPI_RESET_PIN = 12;
#endif

#if OLED_MODE != OLED_OFF
#include <U8g2lib.h>
#include <Preferences.h>
#include <sys/time.h>
#include <mbedtls/base64.h>
#endif

#if OLED_MODE == OLED_I2C_SETUP
#include <Wire.h>

U8G2_SSD1306_128X64_NONAME_F_HW_I2C ssd1306(U8G2_R0, U8X8_PIN_NONE, OLED_SCL_PIN, OLED_SDA_PIN);
U8G2_SH1106_128X64_NONAME_F_HW_I2C sh1106(U8G2_R0, U8X8_PIN_NONE, OLED_SCL_PIN, OLED_SDA_PIN);
U8G2_SSD1309_128X64_NONAME0_F_HW_I2C ssd1309(U8G2_R0, U8X8_PIN_NONE, OLED_SCL_PIN, OLED_SDA_PIN);
U8G2 *const OLED_DRIVERS[] = {&ssd1306, &sh1106, &ssd1309};
const char *const OLED_NAMES[] = {"SSD1306", "SH1106", "SSD1309"};
constexpr uint8_t OLED_DRIVER_COUNT = sizeof(OLED_DRIVERS) / sizeof(OLED_DRIVERS[0]);
Preferences oledSettings;
U8G2 *activeDisplay = nullptr;
uint8_t oledDriver = 0;
uint8_t oledAddress = 0;
#elif OLED_MODE == OLED_SSD1309_I2C
U8G2_SSD1309_128X64_NONAME0_F_HW_I2C oled(U8G2_R0, U8X8_PIN_NONE, OLED_SCL_PIN, OLED_SDA_PIN);
#elif OLED_MODE == OLED_SSD1309_SPI
U8G2_SSD1309_128X64_NONAME0_F_4W_SW_SPI oled(
    U8G2_R0, OLED_SPI_SCK_PIN, OLED_SPI_MOSI_PIN, OLED_SPI_CS_PIN, OLED_SPI_DC_PIN, OLED_SPI_RESET_PIN);
#elif OLED_MODE == OLED_SSD1306_I2C
U8G2_SSD1306_128X64_NONAME_F_HW_I2C oled(U8G2_R0, U8X8_PIN_NONE, OLED_SCL_PIN, OLED_SDA_PIN);
#elif OLED_MODE == OLED_SH1106_I2C
U8G2_SH1106_128X64_NONAME_F_HW_I2C oled(U8G2_R0, U8X8_PIN_NONE, OLED_SCL_PIN, OLED_SDA_PIN);
#elif OLED_MODE != OLED_OFF
#error "Unknown OLED_MODE."
#endif

constexpr char APP_LABELS[CHANNEL_COUNT][8] = {
  "Spotify", "Discord", "Chrome", "Game", "VLC"
};
constexpr uint16_t RAW_MIN[CHANNEL_COUNT] = {35, 35, 35, 35, 35};
constexpr uint16_t RAW_MAX[CHANNEL_COUNT] = {4020, 4020, 4020, 4020, 4020};
constexpr bool REVERSED[CHANNEL_COUNT] = {false, false, false, false, false};
constexpr uint32_t SAMPLE_INTERVAL_MS = 5;
constexpr uint32_t REPORT_INTERVAL_MS = 8;
constexpr uint32_t HEARTBEAT_MS = 250;
constexpr uint32_t DISPLAY_INTERVAL_MS = 100;
constexpr uint32_t KNOB_HOLD_MS = 3000;
constexpr uint32_t FRAME_TIMEOUT_MS = 6500;
constexpr size_t FRAME_BYTES = 128 * 64 / 8;
constexpr size_t FRAME_CHUNK_BYTES = 256;
constexpr uint8_t FRAME_CHUNKS = FRAME_BYTES / FRAME_CHUNK_BYTES;

float filtered[CHANNEL_COUNT] = {};
uint8_t levels[CHANNEL_COUNT] = {};
uint32_t lastSample = 0;
uint32_t lastReport = 0;
uint32_t lastReportAttempt = 0;
bool displayDirty = true;
bool levelsDirty = true;
uint8_t nextDisplayRow = 8;
uint32_t lastDisplay = 0;
char knobLabels[CHANNEL_COUNT][19] = {};
uint8_t lastMovedChannel = 0;
uint32_t lastMovement = 0;
bool showingKnob = false;
bool hasRemoteFrame = false;
uint32_t lastFrame = 0;
uint8_t remoteFrame[FRAME_BYTES];
uint8_t incomingFrame[FRAME_BYTES];
uint8_t nextFrameChunk = 0;
char serialCommand[360];
size_t commandLength = 0;
bool readingCommand = false;

#if OLED_MODE != OLED_OFF
enum DisplayPage : uint8_t { MixerPage, ClockPage, WeatherPage, MediaPage, UptimePage, LegacyPage = 255 };
struct SavedMedia {
  uint32_t id;
  uint8_t pixels[FRAME_BYTES];
};
Preferences displaySettings;
DisplayPage selectedPage = LegacyPage;
DisplayPage savedPage = LegacyPage;
uint8_t displayFlags = 0;
struct SavedMedia savedMedia = {};
uint32_t mediaId = 0;
bool desktopOnline = false;
bool clockValid = false;
uint32_t desktopPing = 0;
uint32_t selectionChanged = 0;
time_t displayedMinute = -1;

void markDesktopOffline() {
  desktopOnline = false;
  showingKnob = false;
  displayDirty = true;
}

void receivePage(const char *message) {
  unsigned page, flags;
  unsigned long long localEpoch;
  unsigned long media;
  if (sscanf(message, "%u,%u,%llu,%lu", &page, &flags, &localEpoch, &media) != 4 || page > UptimePage) return;
  const bool changed = selectedPage != page || mediaId != media;
  if (changed) {
    selectedPage = static_cast<DisplayPage>(page);
    mediaId = static_cast<uint32_t>(media);
    hasRemoteFrame = false;
    nextFrameChunk = 0;
    selectionChanged = millis();
  }
  if (localEpoch >= 1704067200ULL && localEpoch < 7258118400ULL) {
    const time_t value = static_cast<time_t>(localEpoch);
    if (!clockValid || llabs(static_cast<long long>(time(nullptr)) - value) > 2) {
      struct timeval tv = {value, 0};
      settimeofday(&tv, nullptr);
    }
    clockValid = true;
  }
  if (changed || !desktopOnline || displayFlags != (flags & 1)) displayDirty = true;
  displayFlags = static_cast<uint8_t>(flags & 1);
  desktopOnline = true;
  desktopPing = millis();
}
#endif

bool tryWriteSerial(const char *message) {
  const size_t length = strlen(message);
  if (Serial.availableForWrite() < static_cast<int>(length)) {
    return false;
  }
  return Serial.write(reinterpret_cast<const uint8_t *>(message), length) == length;
}

#if OLED_MODE == OLED_I2C_SETUP
uint8_t findOled() {
  for (uint8_t address = 0x3C; address <= 0x3D; ++address) {
    Wire.beginTransmission(address);
    if (Wire.endTransmission() == 0) {
      return address;
    }
  }
  return 0;
}

void startOled() {
  activeDisplay = nullptr;
  oledAddress = findOled();
  if (oledAddress != 0) {
    activeDisplay = OLED_DRIVERS[oledDriver];
    activeDisplay->setI2CAddress(oledAddress << 1);
    activeDisplay->setBusClock(100000);
    activeDisplay->begin();
    displayDirty = true;
  }
}

void handleOledSetupKey(int command) {
  if (command >= '1' && command < '1' + OLED_DRIVER_COUNT) {
    oledDriver = command - '1';
    startOled();
  } else if (command == '?') {
    startOled();
  } else if (command == 's' || command == 'S') {
    if (activeDisplay == nullptr) {
      Serial.println("OLED: cannot save until the display answers on I2C.");
    } else if (oledSettings.putUChar("driver", oledDriver) == 1) {
      Serial.printf("OLED: saved %s.\n", OLED_NAMES[oledDriver]);
    } else {
      Serial.println("OLED: could not save the driver.");
    }
    return;
  } else {
    return;
  }

  if (oledAddress == 0) {
    Serial.printf("OLED: no reply at 0x3C or 0x3D. Check power and SDA=%d, SCL=%d.\n", OLED_SDA_PIN, OLED_SCL_PIN);
  } else {
    Serial.printf("OLED: I2C reply at 0x%02X. Trying %s.\n",
                  static_cast<unsigned>(oledAddress), OLED_NAMES[oledDriver]);
    Serial.println("OLED: 1=SSD1306, 2=SH1106, 3=SSD1309. Send s when bars look right.");
  }
}
#endif

void handleDesktopCommand() {
  if (commandLength == 10 && (serialCommand[0] == 'H' || serialCommand[0] == 'I') && serialCommand[1] == ',') {
    for (size_t i = 2; i < commandLength; ++i) {
      const char c = serialCommand[i];
      if (!((c >= '0' && c <= '9') || (c >= 'A' && c <= 'F'))) return;
    }
    if (serialCommand[0] == 'H') {
      char response[32];
      snprintf(response, sizeof(response), "PURPLEMIXER,%d,%s\n", PROTOCOL_VERSION, serialCommand + 2);
      if (tryWriteSerial(response)) levelsDirty = true;
    } else {
      uint8_t mac[6];
      if (esp_efuse_mac_get_default(mac) != ESP_OK) return;
      const esp_partition_t *running = esp_ota_get_running_partition();
      const esp_partition_t *boot = esp_ota_get_boot_partition();
      char response[144];
      snprintf(response, sizeof(response), "VOLTRA,INFO,%s,%s,%s,%d,%02X%02X%02X%02X%02X%02X,%lu,%lu,%lu\n",
               serialCommand + 2, HARDWARE_ID, FIRMWARE_VERSION, PROTOCOL_VERSION,
               mac[0], mac[1], mac[2], mac[3], mac[4], mac[5],
               static_cast<unsigned long>(ESP.getFlashChipSize()),
               static_cast<unsigned long>(running ? running->address : 0),
               static_cast<unsigned long>(boot ? boot->address : 0));
      tryWriteSerial(response);
    }
    return;
  }
  if (strcmp(serialCommand, "Q") == 0) {
#if OLED_MODE == OLED_OFF
    tryWriteSerial("OLED,ABSENT\n");
#else
#if OLED_MODE == OLED_I2C_SETUP
    if (activeDisplay == nullptr) {
      tryWriteSerial("OLED,ABSENT\n");
      return;
    }
#endif
    tryWriteSerial("OLED,READY,128,64,3\n");
#endif
#if OLED_MODE != OLED_OFF
  } else if (strncmp(serialCommand, "P,", 2) == 0) {
    receivePage(serialCommand + 2);
  } else if (strcmp(serialCommand, "D") == 0) {
    markDesktopOffline();
  } else if (serialCommand[0] == 'C' && commandLength > 3 && serialCommand[2] == ',' &&
             serialCommand[1] >= '0' && serialCommand[1] < '0' + FRAME_CHUNKS) {
    const uint8_t chunk = serialCommand[1] - '0';
    if (chunk == 0) {
      nextFrameChunk = 0;
    }
    if (chunk != nextFrameChunk) {
      return;
    }

    size_t decodedLength = 0;
    if (mbedtls_base64_decode(incomingFrame + chunk * FRAME_CHUNK_BYTES, FRAME_CHUNK_BYTES,
                             &decodedLength, reinterpret_cast<const uint8_t *>(serialCommand + 3),
                             commandLength - 3) != 0 || decodedLength != FRAME_CHUNK_BYTES) {
      return;
    }
    ++nextFrameChunk;
    if (nextFrameChunk == FRAME_CHUNKS) {
      nextFrameChunk = 0;
      bool changed = !hasRemoteFrame || memcmp(remoteFrame, incomingFrame, FRAME_BYTES) != 0;
      memcpy(remoteFrame, incomingFrame, FRAME_BYTES);
      lastFrame = millis();
      hasRemoteFrame = true;
      if (selectedPage == MediaPage && mediaId != 0 && savedMedia.id != mediaId) {
        savedMedia.id = mediaId;
        memcpy(savedMedia.pixels, remoteFrame, FRAME_BYTES);
        displaySettings.putBytes("media", &savedMedia, sizeof savedMedia);
      }
      if (changed && !showingKnob) {
        displayDirty = true;
      }
    }
    char ack[] = "OLED,ACK,0\n";
    ack[9] = '0' + chunk;
    tryWriteSerial(ack);
  } else if (serialCommand[0] == 'L' && commandLength >= 3 &&
             serialCommand[1] >= '1' && serialCommand[1] <= '5' && serialCommand[2] == ',') {
    const uint8_t channel = serialCommand[1] - '1';
    snprintf(knobLabels[channel], sizeof(knobLabels[channel]), "%.18s", serialCommand + 3);
    displayDirty = true;
#endif
  }
}

void readDesktopSerial() {
  for (size_t read = 0; read < 512 && Serial.available() > 0; ++read) {
    const int byte = Serial.read();
    if (byte == '@') {
      commandLength = 0;
      readingCommand = true;
    } else if (readingCommand) {
      if (byte == '\n') {
        if (commandLength < sizeof(serialCommand)) {
          serialCommand[commandLength] = '\0';
          handleDesktopCommand();
        }
        readingCommand = false;
      } else if (byte >= 32 && byte <= 126 && commandLength < sizeof(serialCommand) - 1) {
        serialCommand[commandLength++] = static_cast<char>(byte);
      } else if (byte != '\r') {
        // Discard malformed input until the next command marker.
        commandLength = sizeof(serialCommand);
      }
    }
#if OLED_MODE == OLED_I2C_SETUP
    else {
      handleOledSetupKey(byte);
    }
#endif
  }
}

uint16_t readPot(uint8_t pin) {
  uint32_t sum = 0;
  for (uint8_t sample = 0; sample < 8; ++sample) {
    sum += analogRead(pin);
  }
  return sum / 8;
}

uint8_t toPercent(uint8_t channel, float raw) {
  const float span = RAW_MAX[channel] - RAW_MIN[channel];
  const float position = constrain((raw - RAW_MIN[channel]) / span, 0.0f, 1.0f);
  const uint8_t percent = static_cast<uint8_t>(position * 100.0f + 0.5f);
  return REVERSED[channel] ? 100 - percent : percent;
}

void updateLevels(uint32_t now) {
#if OLED_MODE != OLED_OFF
  int strongestChange = 0;
#else
  (void)now;
#endif
  for (uint8_t channel = 0; channel < CHANNEL_COUNT; ++channel) {
    const float error = readPot(POT_PINS[channel]) - filtered[channel];
    const float gain = fabsf(error) > 90.0f ? 0.72f : 0.28f;
    filtered[channel] += gain * error;
    const uint8_t next = toPercent(channel, filtered[channel]);
    const int difference = abs(static_cast<int>(next) - levels[channel]);
    if (difference >= 2 || (difference && (next == 0 || next == 100))) {
      levels[channel] = next;
      levelsDirty = true;
#if OLED_MODE != OLED_OFF
      if (desktopOnline || selectedPage == LegacyPage) displayDirty = true;
      if (difference > strongestChange) {
        strongestChange = difference;
        lastMovedChannel = channel;
      }
#endif
    }
  }
#if OLED_MODE != OLED_OFF
  if (strongestChange > 0 && (desktopOnline || selectedPage == LegacyPage)) {
    lastMovement = now;
    showingKnob = true;
  }
#endif
}

void drawMixer() {
#if OLED_MODE != OLED_OFF
#if OLED_MODE == OLED_I2C_SETUP
  if (activeDisplay == nullptr) {
    return;
  }
  U8G2 &oled = *activeDisplay;
#endif
  oled.clearBuffer();
  if (showingKnob) {
    const uint8_t channel = lastMovedChannel;
    const uint8_t level = levels[channel];
    const char *label = knobLabels[channel][0] ? knobLabels[channel] : APP_LABELS[channel];
    char percent[5];
    snprintf(percent, sizeof(percent), "%u%%", static_cast<unsigned>(level));
    oled.setFont(u8g2_font_6x12_tr);
    oled.drawStr(4, 13, label);
    oled.setFont(u8g2_font_helvB24_tr);
    oled.drawStr((128 - oled.getStrWidth(percent)) / 2, 45, percent);
    oled.drawFrame(4, 53, 120, 9);
    if (level > 0) {
      oled.drawBox(6, 55, (level * 116 + 50) / 100, 5);
    }
    nextDisplayRow = 0;
    return;
  }
  const bool heldMixer = !desktopOnline && selectedPage == MixerPage && hasRemoteFrame;
  const uint8_t *pixels = nullptr;
  if (hasRemoteFrame && (desktopOnline || selectedPage == LegacyPage || selectedPage == MediaPage || heldMixer))
    pixels = remoteFrame;
  else if (selectedPage == MediaPage && savedMedia.id != 0 && (mediaId == 0 || savedMedia.id == mediaId))
    pixels = savedMedia.pixels;
  if (pixels != nullptr) {
    for (uint8_t y = 0; y < 64; ++y) {
      for (uint8_t x = 0; x < 128; ++x) {
        if (pixels[y * 16 + x / 8] & (0x80 >> (x % 8))) {
          oled.drawPixel(x, y);
        }
      }
    }
    if (heldMixer) {
      oled.setFont(u8g2_font_5x7_tr);
      oled.drawStr(1, 63, "HOLD");
    }
    nextDisplayRow = 0;
    return;
  }

  auto center = [&](uint8_t baseline, const char *text) {
    int x = (128 - static_cast<int>(oled.getStrWidth(text))) / 2;
    oled.drawStr(x < 0 ? 0 : x, baseline, text);
  };
  if (selectedPage == ClockPage || selectedPage == WeatherPage) {
    if (clockValid) {
      time_t current = time(nullptr);
      struct tm local;
      gmtime_r(&current, &local);
      char date[24], clock[12], day[16];
      strftime(date, sizeof date, "%d %b %Y", &local);
      strftime(day, sizeof day, "%A", &local);
      strftime(clock, sizeof clock, displayFlags & 1 ? "%I:%M %p" : "%H:%M", &local);
      oled.setFont(u8g2_font_6x12_tr);
      center(13, day);
      oled.setFont(displayFlags & 1 ? u8g2_font_helvB14_tr : u8g2_font_helvB24_tr);
      center(43, clock);
      oled.setFont(u8g2_font_6x12_tr);
      center(59, date);
    } else {
      oled.setFont(u8g2_font_6x12_tr);
      center(27, "Clock not synced");
      center(45, "Connect to PC");
    }
  } else if (selectedPage != LegacyPage) {
    oled.setFont(u8g2_font_6x12_tr);
    center(25, selectedPage == UptimePage ? "PC uptime" : selectedPage == MediaPage ? "Image / GIF" : "Mixer levels");
    center(43, selectedPage == UptimePage ? "PC unavailable" : "Waiting for PC data");
  } else {
    oled.setFont(u8g2_font_5x7_tr);
    for (uint8_t channel = 0; channel < CHANNEL_COUNT; ++channel) {
      const uint8_t y = 1 + channel * 11 + (channel == 4 ? 1 : 0);
      char label[19];
      snprintf(label, sizeof label, "%s", knobLabels[channel][0] ? knobLabels[channel] : APP_LABELS[channel]);
      size_t length = strlen(label);
      if (oled.getStrWidth(label) > 43) {
        label[--length] = '.';
        while (length > 0 && oled.getStrWidth(label) > 43) {
          label[length--] = 0;
          label[length] = '.';
        }
      }
      oled.drawStr(1, y + 7, label);
      if (channel == 4) oled.drawHLine(1, y - 1, 126);
      oled.drawHLine(46, y + 4, 56);
      const uint8_t fill = (levels[channel] * 56 + 50) / 100;
      if (fill) oled.drawBox(46, y + 3, fill, 3);
      char percent[5];
      snprintf(percent, sizeof percent, "%u%%", static_cast<unsigned>(levels[channel]));
      oled.drawStr(127 - oled.getStrWidth(percent), y + 7, percent);
    }
  }
  nextDisplayRow = 0;
#endif
}

void flushDisplayRow() {
#if OLED_MODE != OLED_OFF
  if (nextDisplayRow >= 8) return;
#if OLED_MODE == OLED_I2C_SETUP
  if (activeDisplay == nullptr) { nextDisplayRow = 8; return; }
  U8G2 &oled = *activeDisplay;
#endif
  oled.updateDisplayArea(0, nextDisplayRow, 16, 1);
  ++nextDisplayRow;
#endif
}

bool reportLevels() {
  char packet[32];
  snprintf(
    packet, sizeof(packet), "MIX,%u,%u,%u,%u,%u\n",
    static_cast<unsigned>(levels[0]), static_cast<unsigned>(levels[1]),
    static_cast<unsigned>(levels[2]), static_cast<unsigned>(levels[3]),
    static_cast<unsigned>(levels[4])
  );
  return tryWriteSerial(packet);
}

void setup() {
  Serial.setRxBufferSize(1024);
  Serial.begin(115200);
#if ARDUINO_USB_CDC_ON_BOOT
  Serial.setTxTimeoutMs(8);
#endif
  analogReadResolution(12);
  analogSetAttenuation(ADC_11db);
#if OLED_MODE != OLED_OFF
  if (displaySettings.begin("display", false)) {
    uint8_t page = displaySettings.getUChar("page", LegacyPage);
    if (page <= UptimePage) selectedPage = savedPage = static_cast<DisplayPage>(page);
    if (displaySettings.getBytesLength("media") == sizeof savedMedia)
      displaySettings.getBytes("media", &savedMedia, sizeof savedMedia);
  }
#endif

  for (uint8_t channel = 0; channel < CHANNEL_COUNT; ++channel) {
    filtered[channel] = readPot(POT_PINS[channel]);
    levels[channel] = toPercent(channel, filtered[channel]);
  }

#if OLED_MODE == OLED_I2C_SETUP
  if (oledSettings.begin("mixer-oled", false)) {
    const uint8_t savedDriver = oledSettings.getUChar("driver", 0);
    if (savedDriver < OLED_DRIVER_COUNT) {
      oledDriver = savedDriver;
    }
  }
  Wire.begin(OLED_SDA_PIN, OLED_SCL_PIN, 100000);
  Wire.setTimeOut(15);
  startOled();
#elif OLED_MODE != OLED_OFF
#if OLED_MODE != OLED_SSD1309_SPI
  oled.setI2CAddress(OLED_ADDRESS << 1);
#endif
  oled.begin();
#endif
}

void loop() {
  readDesktopSerial();
  const uint32_t now = millis();
  if (now - lastSample >= SAMPLE_INTERVAL_MS) {
    lastSample = now;
    updateLevels(now);
  }
#if OLED_MODE != OLED_OFF
  if (showingKnob && now - lastMovement >= KNOB_HOLD_MS) {
    showingKnob = false;
    displayDirty = true;
  }
  if (selectedPage == LegacyPage && hasRemoteFrame && now - lastFrame >= FRAME_TIMEOUT_MS) {
    hasRemoteFrame = false;
    displayDirty = true;
  }
  if (desktopOnline && now - desktopPing >= FRAME_TIMEOUT_MS) markDesktopOffline();
  if (!desktopOnline && clockValid && (selectedPage == ClockPage || selectedPage == WeatherPage)) {
    time_t minute = time(nullptr) / 60;
    if (minute != displayedMinute) {
      displayedMinute = minute;
      displayDirty = true;
    }
  }
  if (selectedPage != savedPage && now - selectionChanged > 2500) {
    if (displaySettings.putUChar("page", selectedPage) == 1) savedPage = selectedPage;
    else selectionChanged = now;
  }
#endif

  const uint32_t elapsed = now - lastReport;
  if (now - lastReportAttempt >= REPORT_INTERVAL_MS && (levelsDirty || elapsed >= HEARTBEAT_MS)) {
    lastReportAttempt = now;
    if (reportLevels()) {
      lastReport = now;
      levelsDirty = false;
    }
  }

  if (nextDisplayRow == 8 && displayDirty && now - lastDisplay >= DISPLAY_INTERVAL_MS) {
    lastDisplay = now;
    drawMixer();
    displayDirty = false;
  }
  flushDisplayRow();
  delay(1);
}
