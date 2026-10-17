# Accessible YouTube Player - Manual Screen Reader & Accessibility Verification Checklist

This checklist provides step-by-step verification procedures for assistive technologies, including **NVDA (NonVisual Desktop Access)**, **JAWS for Windows**, and **Windows Narrator**.

---

## 1. Screen Reader Setup & Preparation
- [ ] Test with **NVDA** (v2023.1 or newer) running with default speech synthesizer.
- [ ] Test with **Windows Narrator** (`Win + Ctrl + Enter`).
- [ ] Test with **JAWS** (if available) or verify with Inspect.exe / Accessibility Insights for Windows.

---

## 2. Window Launch & Initial Focus
- [ ] **Initial Focus**: On application launch, keyboard focus must land cleanly in the search input box (`Search or URL`).
- [ ] **Title & Window Announcement**: Screen reader must announce `"Accessible YouTube Player"` without spurious focus bounce.
- [ ] **Search Box Announcement**: Screen reader must read:
  - Accessible Name: `"Search query or video link"`
  - Help text: `"Enter YouTube video title keywords or paste a video link, then press Enter or Alt+S"`

---

## 3. Keyboard-Only Navigation & Tab Order
- [ ] **Tab Navigation Cycle**:
  1. Search Box (`TextBox`)
  2. Search Button (`Button`, `Alt+S`)
  3. Search Results (`ListView`)
  4. Play / Pause Button (`Space`)
  5. Seek Backward 5s (`Left Arrow`)
  6. Seek Forward 5s (`Right Arrow`)
  7. Volume Slider (`Control + Up/Down`)
  8. Speed Down / Up Buttons (`[` and `]`)
- [ ] **Focus Visuals**: Every interactive control must display a visible, high-contrast focus border (checked for both Light and Windows High Contrast themes).
- [ ] **Shift+Tab**: Reverse tab order must follow the exact reverse path with no focus traps.

---

## 4. Search Execution & Live Announcements
- [ ] Type query (e.g. `mozart`) and press `Enter`.
- [ ] **Live Announcement**: Screen reader must announce `"Searching for mozart..."` without moving focus.
- [ ] **Completion Announcement**: On response, screen reader announces `"X videos found for mozart. Use down arrow to navigate results."`
- [ ] **Direct URL Paste**: Pasting a full YouTube URL (e.g. `https://www.youtube.com/watch?v=...`) directly resolves that video and announces its title.
- [ ] **Error Handling**: Disconnecting internet or entering an invalid query must announce a clear, actionable message (e.g. `"Search failed: Public servers are currently unresponsive..."`) instead of a raw stack trace.

---

## 5. Search Results Navigation (`ListView`)
- [ ] Press `Tab` to reach the search results list.
- [ ] Use `Up Arrow` and `Down Arrow` to navigate items.
- [ ] **Accessible Summary Readout**: Moving to each video must speak the full accessible summary formatted as:
  `"{Title}, by {Channel}, duration {Duration}, {Views} views, uploaded {Date}"`
  *(e.g., "Mozart - Lacrimosa, by Classical Masterpieces, duration 3:15, 1,200,000 views, uploaded 1 year ago")*.
- [ ] **Item Selection & Activation**: Pressing `Enter` on any item triggers playback loading and speaks:
  `"Now playing: {Title}. Press Space to pause, Left and Right arrows to seek."`

---

## 6. Media Playback Controls & Key Chords
- [ ] **Spacebar**: Toggles Play/Pause. Announces `"Playing"` or `"Paused"`.
- [ ] **Left Arrow**: Seeks backward 5s. Announces new position (e.g. `"Seek 01:25"`).
- [ ] **Right Arrow**: Seeks forward 5s. Announces new position (e.g. `"Seek 01:35"`).
- [ ] **Control + Up Arrow**: Increases volume by 5%. Announces `"Volume 85 percent"`.
- [ ] **Control + Down Arrow**: Decreases volume by 5%. Announces `"Volume 80 percent"`.
- [ ] **Right Bracket (`]`)**: Increases speed by 0.1x. Announces `"Speed 1.1x"`.
- [ ] **Left Bracket (`[`)**: Decreases speed by 0.1x. Announces `"Speed 1.0x"`.

---

## 7. Dynamic Backend Failover & Status
- [ ] When primary provider (Piped) experiences downtime or timeout, verify the app automatically switches to Invidious.
- [ ] Live region announces: `"Active backend switched to Invidious."`
- [ ] Verify search and stream resolution continue uninterrupted without user intervention.
- [ ] If both Piped and Invidious are offline, verify graceful fallback to local `yt-dlp` extraction.

---

## 8. High Contrast & UI Automation Inspection
- [ ] Turn on Windows High Contrast Mode (`Left Alt + Left Shift + PrintScreen`).
- [ ] Verify all borders, text, and selected items maintain contrast ratio $\ge 7:1$.
- [ ] Inspect elements using `Accessibility Insights for Windows`:
  - Verify zero `ControlType.Custom` without names.
  - Verify `AutomationProperties.Name` is populated on every control.
  - Verify `IsKeyboardFocusable` is true for all controls.
