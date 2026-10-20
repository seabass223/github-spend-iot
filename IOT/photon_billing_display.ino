// Particle Photon + Tayda A-1738 common-anode 4-digit display
// Fetches the already-rounded integer at:
//   http://192.168.1.229:7007/me-this-month
//
// Network design: This original Photon uses plain HTTP only on the trusted LAN.
// The LAN server owns the GitHub HTTPS request and GitHub token; no credential is
// stored in this firmware or sent to the display.
//
// Display wiring (confirmed against A-1738 datasheet):
// D0--2.2k--pin 11(A), D1--2.2k--pin 7(B),  D2--2.2k--pin 4(C),
// D3--2.2k--pin 2(D),  D4--2.2k--pin 1(E),  D5--2.2k--pin 10(F),
// D6--2.2k--pin 5(G),  D7--2.2k--pin 3(DP).
// A0--pin 12(DIG1), A1--pin 9(DIG2), A2--pin 8(DIG3), A3--pin 6(DIG4).

SYSTEM_THREAD(ENABLED);

constexpr pin_t SEGMENT_PINS[] = {D0, D1, D2, D3, D4, D5, D6, D7};
constexpr pin_t DIGIT_PINS[] = {A0, A1, A2, A3};
constexpr pin_t REFRESH_BUTTON_PIN = A4;
constexpr pin_t RED_LED_PIN = A5;
constexpr pin_t YELLOW_LED_PIN = RX;
constexpr pin_t GREEN_LED_PIN = TX;
constexpr uint8_t DIGIT_COUNT = 4;
constexpr uint16_t SCAN_INTERVAL_US = 2000;  // 125 Hz full display refresh

IPAddress SERVER(192, 168, 1, 229);
constexpr uint16_t SERVER_PORT = 7007;
constexpr char DOLLARS_PATH[] = "/me-this-month";
constexpr char TRAFFIC_PATH[] = "/ai-credits-used-traffic-light";
constexpr unsigned long SUCCESS_REFRESH_MS = 300000; // automatic refresh every 5 minutes
constexpr unsigned long ERROR_RETRY_MS = 15000;
constexpr unsigned long REQUEST_TIMEOUT_MS = 15000;

// Bits: dp g f e d c b a. Set bit means illuminate the segment.
constexpr uint8_t DIGIT_GLYPHS[10] = {
    0b00111111, 0b00000110, 0b01011011, 0b01001111, 0b01100110,
    0b01101101, 0b01111101, 0b00000111, 0b01111111, 0b01101111
};
constexpr uint8_t CENTER_ONLY = 0b01000000;
constexpr uint8_t OUTER_SPINNER[] = {
    0b00000001, // A
    0b00000010, // B
    0b00000100, // C
    0b00001000, // D
    0b00010000, // E
    0b00100000  // F
};

enum class DisplayMode : uint8_t { FETCHING, VALUE, ERROR };
DisplayMode displayMode = DisplayMode::FETCHING;

TCPClient client;
bool requestActive = false;
bool sawStatusLine = false;
bool statusOk = false;
bool headersComplete = false;
char headerLine[96];
size_t headerLength = 0;
char responseBody[20];
size_t bodyLength = 0;
unsigned long requestStartedAt = 0;
unsigned long nextRequestAt = 0;
unsigned int displayedDollars = 0;
unsigned int savedDollars = 0;
bool fetchingTraffic = false;
unsigned int trafficLightValue = 255;
bool refreshButtonLatched = false;
unsigned long lastManualRefreshAt = 0;

uint8_t scanDigit = 0;
unsigned long lastScanAt = 0;

void allDigitsOff() {
    for (pin_t pin : DIGIT_PINS) {
        digitalWrite(pin, LOW); // common-anode display: LOW disables a digit
    }
}

void setSegments(uint8_t mask) {
    for (uint8_t i = 0; i < 8; ++i) {
        const bool on = mask & (1U << i);
        digitalWrite(SEGMENT_PINS[i], on ? LOW : HIGH); // LOW sinks LED current
    }
}

uint8_t maskForDigit(uint8_t position) {
    if (displayMode == DisplayMode::ERROR) {
        return CENTER_ONLY;
    }
    if (displayMode == DisplayMode::FETCHING) {
        if (position != 3) {
            return 0;
        }
        return OUTER_SPINNER[(millis() / 125) % 6];
    }

    // VALUE: blank leading positions; always render the ones position.
    const unsigned int divisor[] = {1000, 100, 10, 1};
    const unsigned int place = divisor[position];
    if (position < 3 && displayedDollars < place) {
        return 0;
    }
    return DIGIT_GLYPHS[(displayedDollars / place) % 10];
}

void serviceDisplay() {
    const unsigned long now = micros();
    if (static_cast<unsigned long>(now - lastScanAt) < SCAN_INTERVAL_US) {
        return;
    }
    lastScanAt = now;
    allDigitsOff(); // eliminates ghosting during segment changes
    setSegments(maskForDigit(scanDigit));
    digitalWrite(DIGIT_PINS[scanDigit], HIGH);
    scanDigit = (scanDigit + 1) % DIGIT_COUNT;
}

void scheduleError() {
    client.stop();
    requestActive = false;
    displayMode = DisplayMode::ERROR;
    nextRequestAt = millis() + ERROR_RETRY_MS;
}

bool parseDollars() {
    // Endpoint contract is a raw non-negative integer (possibly followed by CR/LF).
    size_t i = 0;
    while (i < bodyLength && isspace(static_cast<unsigned char>(responseBody[i]))) {
        ++i;
    }
    if (i == bodyLength) {
        return false;
    }

    unsigned long value = 0;
    bool sawDigit = false;
    for (; i < bodyLength; ++i) {
        const char c = responseBody[i];
        if (c >= '0' && c <= '9') {
            sawDigit = true;
            value = value * 10 + static_cast<unsigned long>(c - '0');
            if (value > 9999) {
                return false; // display cannot represent it
            }
        } else if (!isspace(static_cast<unsigned char>(c))) {
            return false;
        }
    }
    if (!sawDigit) {
        return false;
    }
    displayedDollars = static_cast<unsigned int>(value);
    return true;
}

void startRequest(bool traffic);

void setTrafficLight(unsigned int value) {
    trafficLightValue = value;
    analogWrite(RED_LED_PIN, 0); // stop any prior PWM output before a steady state
    digitalWrite(RED_LED_PIN, value == 2 ? HIGH : LOW);
    digitalWrite(YELLOW_LED_PIN, value == 1 ? HIGH : LOW);
    digitalWrite(GREEN_LED_PIN, value == 0 ? HIGH : LOW);
}

void serviceTrafficLight() {
    if (trafficLightValue != 3) {
        return;
    }
    // 2-second triangle-wave "breathing" pulse: 8..255 avoids a harsh fully-off step.
    const unsigned long phase = millis() % 2000;
    const unsigned long rising = phase < 1000 ? phase : 2000 - phase;
    const uint8_t brightness = 8 + (rising * 247UL) / 1000UL;
    analogWrite(RED_LED_PIN, brightness);
}

void finishRequest() {
    client.stop();
    requestActive = false;
    if (!statusOk || !parseDollars()) {
        scheduleError();
        return;
    }
    if (!fetchingTraffic) {
        savedDollars = displayedDollars;
        startRequest(true); // spinner continues during the second request
        return;
    }
    if (displayedDollars > 3) {
        scheduleError();
        return;
    }
    const unsigned int trafficValue = displayedDollars;
    displayedDollars = savedDollars;
    setTrafficLight(trafficValue);
    displayMode = DisplayMode::VALUE;
    nextRequestAt = millis() + SUCCESS_REFRESH_MS;
}

void consumeResponseByte(char c) {
    if (!headersComplete) {
        if (c == '\r') {
            return;
        }
        if (c == '\n') {
            headerLine[headerLength] = '\0';
            if (!sawStatusLine) {
                sawStatusLine = true;
                statusOk = strncmp(headerLine, "HTTP/", 5) == 0 &&
                           headerLine[9] == '2'; // HTTP/x.y 2xx
            } else if (headerLength == 0) {
                headersComplete = true;
            }
            headerLength = 0;
            return;
        }
        if (headerLength >= sizeof(headerLine) - 1) {
            scheduleError();
            return;
        }
        headerLine[headerLength++] = c;
        return;
    }

    if (bodyLength >= sizeof(responseBody) - 1) {
        scheduleError();
        return;
    }
    responseBody[bodyLength++] = c;
}

void startRequest(bool traffic = false) {
    if (!WiFi.ready()) {
        scheduleError();
        return;
    }

    displayMode = DisplayMode::FETCHING;
    fetchingTraffic = traffic;
    sawStatusLine = false;
    statusOk = false;
    headersComplete = false;
    headerLength = 0;
    bodyLength = 0;
    requestStartedAt = millis();

    if (!client.connect(SERVER, SERVER_PORT)) {
        scheduleError();
        return;
    }

    // HTTP/1.0 makes the Kestrel server close the response rather than using
    // chunked transfer encoding; body parsing therefore stays small and robust.
    client.print("GET ");
    client.print(traffic ? TRAFFIC_PATH : DOLLARS_PATH);
    client.print(" HTTP/1.0\r\nHost: 192.168.1.229:7007\r\nConnection: close\r\n\r\n");
    requestActive = true;
}

void requestManualRefresh() {
    // Abort any in-flight request and start a fresh one on the next network pass.
    client.stop();
    requestActive = false;
    displayMode = DisplayMode::FETCHING;
    nextRequestAt = 0;
}

void serviceRefreshButton() {
    const bool pressed = digitalRead(REFRESH_BUTTON_PIN) == LOW;
    const unsigned long now = millis();

    if (pressed && !refreshButtonLatched &&
        static_cast<unsigned long>(now - lastManualRefreshAt) >= 500) {
        refreshButtonLatched = true;
        lastManualRefreshAt = now;
        requestManualRefresh();
    }
    if (!pressed) {
        refreshButtonLatched = false;
    }
}

void serviceNetwork() {
    const unsigned long now = millis();
    if (!requestActive) {
        if (static_cast<long>(now - nextRequestAt) >= 0) {
            startRequest();
        }
        return;
    }

    while (requestActive && client.available()) {
        consumeResponseByte(static_cast<char>(client.read()));
    }
    if (!requestActive) {
        return; // consumeResponseByte detected an error
    }
    if (!client.connected() && !client.available()) {
        finishRequest();
    } else if (static_cast<unsigned long>(now - requestStartedAt) > REQUEST_TIMEOUT_MS) {
        scheduleError();
    }
}

void setup() {
    for (pin_t pin : SEGMENT_PINS) {
        pinMode(pin, OUTPUT);
        digitalWrite(pin, HIGH);
    }
    for (pin_t pin : DIGIT_PINS) {
        pinMode(pin, OUTPUT);
        digitalWrite(pin, LOW);
    }
    pinMode(REFRESH_BUTTON_PIN, INPUT_PULLUP);
    pinMode(RED_LED_PIN, OUTPUT);
    pinMode(YELLOW_LED_PIN, OUTPUT);
    pinMode(GREEN_LED_PIN, OUTPUT);
    digitalWrite(RED_LED_PIN, LOW);
    digitalWrite(YELLOW_LED_PIN, LOW);
    digitalWrite(GREEN_LED_PIN, LOW);
    nextRequestAt = 0; // fetch immediately after boot
}

void loop() {
    serviceDisplay();
    serviceRefreshButton();
    serviceNetwork();
    serviceTrafficLight();
}
