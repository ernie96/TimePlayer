# Changelog

The release workflow publishes the section for each version as its release notes, and
TimePlayer reads them out when it offers that update. Keep each entry short and plain.

## 1.1.0

- TimePlayer now updates itself. It checks for a new version when it starts, tells you what changed, and installs it if you choose Yes. Updates are signed, and TimePlayer refuses any download that is not signed by its publisher.
- Settings has "Check for TimePlayer updates" and an option to turn off the startup check.

## 1.0.1

- Playing a video again in the same session now continues where you left off.
- Videos whose stream comes without captions now get their captions looked up separately. C says plainly when a video has none.
- More reliable with NVDA: the braille add-on no longer queries NVDA ten times a second, and captions are spoken without making TimePlayer wait on NVDA.

## 1.0.0

- First release: search, Featured, What's New, Channels, History, Watch Later and Queue; Up next with autoplay; comments with replies; downloads; share links; braille progress through NVDA.
