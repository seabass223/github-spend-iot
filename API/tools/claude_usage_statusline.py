"""Claude Code status line that also saves your Pro/Max usage limits for the LCD server.

Claude Code runs this command and passes session data as JSON on stdin. For Pro, Max and Ultra
plans that data includes a documented `rate_limits` object (5-hour and 7-day windows). This
script saves that object to ~/.claude/usage-limits.json, which the server's /usage-remaining
endpoint reads. No login token is read or sent anywhere.

Enable it in ~/.claude/settings.json (add " work" after the script path in the work profile's
settings so it writes usage-limits-work.json instead):
  "statusLine": {"type": "command",
                 "command": "python -I C:/repos/github-spend-iot/API/tools/claude_usage_statusline.py"}
"""
import json
import os
import sys
import tempfile
import time

# Optional first argument names the account, so work and personal Claude Code don't overwrite each
# other: no argument -> usage-limits.json (personal), "work" -> usage-limits-work.json.
ACCOUNT = sys.argv[1] if len(sys.argv) > 1 else ""
SUFFIX = f"-{ACCOUNT}" if ACCOUNT else ""
OUT = os.path.join(os.path.expanduser("~"), ".claude", f"usage-limits{SUFFIX}.json")


def save(rate_limits: dict) -> None:
    payload = {"captured_at": int(time.time()), "rate_limits": rate_limits}
    folder = os.path.dirname(OUT)
    fd, tmp = tempfile.mkstemp(dir=folder, prefix=".usage-limits-", suffix=".tmp")
    with os.fdopen(fd, "w", encoding="utf-8") as f:
        json.dump(payload, f)
    os.replace(tmp, OUT)  # atomic, so the server never reads half a file


def main() -> None:
    try:
        data = json.load(sys.stdin)
    except ValueError:
        return

    limits = data.get("rate_limits")

    # Heartbeat for troubleshooting: when the status line last ran, and whether Claude Code
    # included usage limits. Overwritten each run; no session content is recorded.
    try:
        with open(OUT.replace(f"usage-limits{SUFFIX}.json", f"usage-statusline-last-run{SUFFIX}.txt"), "w", encoding="utf-8") as f:
            f.write(f"{time.strftime('%Y-%m-%d %H:%M:%S')} rate_limits={'yes' if limits else 'no'}\n")
    except OSError:
        pass

    if isinstance(limits, dict) and limits:
        try:
            save(limits)
        except OSError:
            pass  # never break the status line over a failed save

    model = (data.get("model") or {}).get("display_name", "Claude")
    week = (limits or {}).get("seven_day") or {}
    used = week.get("used_percentage")
    print(f"[{model}]" + (f" week {100 - used:.0f}% left" if used is not None else ""))


if __name__ == "__main__":
    main()
