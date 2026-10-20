// Particle Photon (original Wi-Fi model) + 0.56-inch 4-digit COMMON-ANODE LED display
// Resistor-only, direct-drive circuit. Displays: 1234
//
// USE EXACTLY EIGHT 2.2 kOhm RESISTORS: one per segment a,b,c,d,e,f,g,dp.
// The resistors are required. Do NOT connect any display LED pin directly to a Photon pin.
//
// This low-current arrangement is intentionally modest in brightness. It keeps the direct
// GPIO circuit conservative: at most ~1 mA is sunk by a segment pin and ~5 mA sourced by a
// digit pin. For a much brighter display, use digit-driver transistors instead.
//
// Display cathode connections, EACH THROUGH ITS OWN 2.2 kOhm resistor:
//   D0=a, D1=b, D2=c, D3=d, D4=e, D5=f, D6=g, D7=decimal point
// Display common-anode digit connections, DIRECT (no resistor):
//   A0=DIG1 (leftmost), A1=DIG2, A2=DIG3, A3=DIG4 (rightmost)
//
// Confirmed for Tayda part A-1738 (Everstar ESD-056BG4BW-CAS1), front face
// toward you with decimal points at the bottom:
//   bottom row, left -> right: pins 1(E), 2(D), 3(DP), 4(C), 5(G), 6(DIG4)
//   top row,    left -> right: pins 12(DIG1), 11(A), 10(F), 9(DIG2), 8(DIG3), 7(B)
// DIG1 is the leftmost digit; DIG4 is the rightmost digit.

SYSTEM_THREAD(ENABLED);

constexpr pin_t SEGMENT_PINS[] = {D0, D1, D2, D3, D4, D5, D6, D7};
constexpr pin_t DIGIT_PINS[]   = {A0, A1, A2, A3};
constexpr uint8_t DIGIT_COUNT = 4;
constexpr uint16_t DIGIT_ON_TIME_US = 2500; // 100 Hz whole-display refresh

// Bit order: dp g f e d c b a. A 1 illuminates that segment.
constexpr uint8_t NUMBER_SEGMENTS[10] = {
    0b00111111, 0b00000110, 0b01011011, 0b01001111, 0b01100110,
    0b01101101, 0b01111101, 0b00000111, 0b01111111, 0b01101111
};
constexpr uint8_t DISPLAY_DIGITS[DIGIT_COUNT] = {1, 2, 3, 4};

void allDigitsOff() {
    for (uint8_t i = 0; i < DIGIT_COUNT; ++i) {
        digitalWrite(DIGIT_PINS[i], LOW); // common-anode digit is off at LOW
    }
}

void setSegments(uint8_t glyph) {
    for (uint8_t segment = 0; segment < 8; ++segment) {
        const bool illuminate = glyph & (1 << segment);
        // Common-anode LEDs illuminate when their cathode is driven LOW.
        digitalWrite(SEGMENT_PINS[segment], illuminate ? LOW : HIGH);
    }
}

void setup() {
    for (pin_t pin : SEGMENT_PINS) {
        pinMode(pin, OUTPUT);
        digitalWrite(pin, HIGH); // cathodes inactive
    }
    for (pin_t pin : DIGIT_PINS) {
        pinMode(pin, OUTPUT);
    }
    allDigitsOff();
}

void loop() {
    for (uint8_t digit = 0; digit < DIGIT_COUNT; ++digit) {
        allDigitsOff(); // avoid ghosting while segment state changes
        setSegments(NUMBER_SEGMENTS[DISPLAY_DIGITS[digit]]);
        digitalWrite(DIGIT_PINS[digit], HIGH); // source this digit's common anode
        delayMicroseconds(DIGIT_ON_TIME_US);
    }
}
