# TimePlayer Braille progress track - NVDA global plugin.
# Installed and kept up to date by TimePlayer (NvdaAddonInstaller.cs). Do not edit the installed copy.
#
# While TimePlayer is the foreground app and a video is playing or paused, this plugin takes the
# braille display away from NVDA (braille.decide_enabled, the same hook NVDA Remote uses) and draws
# the progress track itself:
#   playing -> blank display with a lower g (dots 2-3-5-6) at the current position
#   paused  -> the lower g blinks slowly
# Cursor routing keys are captured before NVDA's global commands, so they never reach
# braille.handler.routeTo / doAction on the focused button. They post the cell index and the
# display width to TimePlayer, which seeks silently.
#
# The display is handed back to NVDA as soon as TimePlayer stops, loses the foreground, or focus
# lands in an editable text field (so typing in the search box shows normal braille).
#
# IPC with TimePlayer (both files in %TEMP%):
#   timeplayer_hwnd.txt          - main window handle, written on startup
#   timeplayer_braille_state.txt - "playing 0.4231", "paused 0.4231", "stopped" or "off"

import os
import time

import api
import braille
import controlTypes
import globalPluginHandler
import gui
import inputCore
import keyboardHandler
import winUser
from logHandler import log

_TEMP_DIR = os.environ.get("TEMP") or os.environ.get("TMP") or ""
_STATE_FILE = os.path.join(_TEMP_DIR, "timeplayer_braille_state.txt")
_HWND_FILE = os.path.join(_TEMP_DIR, "timeplayer_hwnd.txt")

WM_TIMEPLAYER_BRAILLE_TOUCH = winUser.WM_USER + 777
WM_TIMEPLAYER_TOGGLE_BRAILLE = winUser.WM_USER + 778
# NVDA sorts the parts of identifiers ("kb:control+d+shift"), so normalise ours the same way.
_TOGGLE_GESTURE = inputCore.normalizeGestureIdentifier("kb:control+shift+d")

# Dots 2-3-5-6 (U+2836 - U+2800).
_LOWER_G = 0x36
_POLL_MS = 100
_BLINK_MS = 600


class _WatchedFile:
	"""Re-reads a small text file only when its modification time or size changes."""

	def __init__(self, path):
		self._path = path
		self._stamp = None
		self.text = ""

	def refresh(self):
		try:
			st = os.stat(self._path)
		except OSError:
			self._stamp = None
			self.text = ""
			return
		stamp = (st.st_mtime_ns, st.st_size)
		if stamp == self._stamp:
			return
		try:
			with open(self._path, "r", encoding="utf-8", errors="replace") as f:
				self.text = f.read().strip()
			self._stamp = stamp
		except OSError:
			# TimePlayer is mid-write; try again on the next tick.
			pass


class GlobalPlugin(globalPluginHandler.GlobalPlugin):
	def __init__(self):
		super().__init__()
		self._stateFile = _WatchedFile(_STATE_FILE)
		self._hwndFile = _WatchedFile(_HWND_FILE)
		self._hwnd = 0
		self._pid = 0
		self._state = "off"
		self._fraction = 0.0
		self._blinkEpoch = time.monotonic()
		self._owning = False
		self._lastCells = None
		self._lastDisplay = None
		self._needsRedraw = False
		braille.decide_enabled.register(self._decideBrailleEnabled)
		braille.pre_writeCells.register(self._onPreWriteCells)
		self._timer = gui.NonReEntrantTimer(self._tick)
		self._timer.Start(_POLL_MS)
		log.info("TimePlayer braille add-on loaded")

	def terminate(self):
		try:
			self._timer.Stop()
		except Exception:
			pass
		self._setOwning(False)
		braille.decide_enabled.unregister(self._decideBrailleEnabled)
		braille.pre_writeCells.unregister(self._onPreWriteCells)
		super().terminate()

	# --- NVDA extension points ---

	def _decideBrailleEnabled(self):
		return not self._owning

	def _onPreWriteCells(self, cells=None, **kwargs):
		# Something else (a cursor blink, a pan key, a driver reconnect) wrote to the display
		# while we own it; put the progress track back on the next tick.
		if self._owning and cells is not None and list(cells) != self._lastCells:
			self._needsRedraw = True

	# --- Polling ---

	def _tick(self):
		try:
			self._readIpc()
			self._setOwning(self._shouldOwn())
			if self._owning:
				self._draw()
		except Exception:
			log.debugWarning("TimePlayer braille tick failed", exc_info=True)

	def _readIpc(self):
		self._hwndFile.refresh()
		try:
			hwnd = int(self._hwndFile.text or 0)
		except ValueError:
			hwnd = 0
		if hwnd and not winUser.isWindow(hwnd):
			hwnd = 0
		if hwnd != self._hwnd:
			self._hwnd = hwnd
			self._pid = winUser.getWindowThreadProcessID(hwnd)[0] if hwnd else 0

		self._stateFile.refresh()
		parts = self._stateFile.text.split()
		state = parts[0].lower() if parts else "off"
		fraction = self._fraction
		if len(parts) > 1:
			try:
				fraction = min(max(float(parts[1]), 0.0), 1.0)
			except ValueError:
				pass
		if state == "paused" and self._state != "paused":
			self._blinkEpoch = time.monotonic()
		self._state = state
		self._fraction = fraction

	def _shouldOwn(self):
		if self._state not in ("playing", "paused") or not self._pid:
			return False
		handler = braille.handler
		if handler is None or not handler.displaySize:
			return False
		if not self._isTimePlayerForeground():
			return False
		focus = api.getFocusObject()
		if focus is not None:
			states = focus.states
			if controlTypes.State.EDITABLE in states and controlTypes.State.READONLY not in states:
				return False
		return True

	def _isTimePlayerForeground(self):
		if not self._pid:
			return False
		fg = winUser.getForegroundWindow()
		return bool(fg) and winUser.getWindowThreadProcessID(fg)[0] == self._pid

	def _setOwning(self, owning):
		if owning == self._owning:
			return
		log.debug(f"TimePlayer braille: {'taking' if owning else 'releasing'} the display")
		self._owning = owning
		self._lastCells = None
		handler = braille.handler
		if handler is None:
			return
		# Re-evaluate decide_enabled now; on False NVDA stops its cursor blink and drops any message.
		handler._refreshEnabled()
		if not owning:
			# NVDA ignored focus/caret changes while disabled, so rebuild its view of the focus.
			try:
				handler.handleGainFocus(api.getFocusObject())
			except Exception:
				log.debugWarning("TimePlayer braille: could not restore NVDA braille", exc_info=True)

	def _draw(self):
		handler = braille.handler
		size = handler.displaySize
		cells = [0] * size
		visible = self._state == "playing" or (
			int((time.monotonic() - self._blinkEpoch) * 1000 / _BLINK_MS) % 2 == 0
		)
		if visible and size:
			cells[round(self._fraction * (size - 1))] = _LOWER_G
		if handler.display is not self._lastDisplay:
			# The driver was reloaded (e.g. Bluetooth reconnect); its cells are unknown.
			self._lastDisplay = handler.display
			self._needsRedraw = True
		if cells == self._lastCells and not self._needsRedraw:
			return
		self._lastCells = cells
		self._needsRedraw = False
		handler._writeCells(cells)

	# --- Cursor routing ---

	def getScript(self, gesture):
		if (
			self._owning
			and isinstance(gesture, braille.BrailleDisplayGesture)
			and gesture.routingIndex is not None
		):
			return self.script_timePlayerSeek
		if (
			isinstance(gesture, keyboardHandler.KeyboardInputGesture)
			and _TOGGLE_GESTURE in gesture.normalizedIdentifiers
			and self._hwnd
			and self._isTimePlayerForeground()
		):
			return self.script_timePlayerToggleBraille
		return super().getScript(gesture)

	def script_timePlayerToggleBraille(self, gesture):
		# Handled here so it works whatever has focus in TimePlayer (including the native video
		# window). TimePlayer flips the setting, speaks it and rewrites the state file.
		winUser.PostMessage(self._hwnd, WM_TIMEPLAYER_TOGGLE_BRAILLE, 0, 0)
		if self._owning:
			self._state = "off"
			self._setOwning(False)

	def script_timePlayerSeek(self, gesture):
		size = braille.handler.displaySize
		index = gesture.routingIndex
		if not self._hwnd or size < 2 or index is None or not 0 <= index < size:
			return
		winUser.PostMessage(self._hwnd, WM_TIMEPLAYER_BRAILLE_TOUCH, index, size)
		# Move the g right away instead of waiting for TimePlayer's next position update.
		self._fraction = index / (size - 1)
		self._blinkEpoch = time.monotonic()
		self._draw()
