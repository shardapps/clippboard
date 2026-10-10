"""Validate the static policy site without installing dependencies."""
from html.parser import HTMLParser
from pathlib import Path
from urllib.parse import unquote, urlsplit


ROOT = Path(__file__).resolve().parent.parent / "site"


class Page(HTMLParser):
    def __init__(self):
        super().__init__()
        self.links = []
        self.ids = set()
        self.h1 = 0
        self.title = 0
        self.viewport = False
        self.language = False

    def handle_starttag(self, tag, attrs):
        attrs = dict(attrs)
        assert tag not in {"script", "iframe", "form"}, f"Unexpected active content: {tag}"
        assert not any(key.startswith("on") for key in attrs), "Inline event handler"
        if "id" in attrs:
            assert attrs["id"] not in self.ids, "Duplicate ID"
            self.ids.add(attrs["id"])
        self.h1 += tag == "h1"
        self.title += tag == "title"
        self.viewport |= tag == "meta" and attrs.get("name") == "viewport"
        self.language |= tag == "html" and attrs.get("lang") == "en"
        for key in ("href", "src"):
            if key in attrs:
                self.links.append(attrs[key])


pages = {}
for path in sorted(ROOT.rglob("*.html")):
    page = Page()
    page.feed(path.read_text(encoding="utf-8"))
    assert page.h1 == page.title == 1 and page.viewport and page.language, path
    assert "main" in page.ids, f"Missing skip-link target: {path}"
    pages[path.resolve()] = page

assert len(pages) == 2, "Expected home and policy pages"
for path, page in pages.items():
    for link in page.links:
        url = urlsplit(link)
        if url.scheme or url.netloc:
            assert url.scheme == "https" and url.netloc, f"Unsafe URL: {link}"
            continue
        target = (path.parent / unquote(url.path)).resolve() if url.path else path
        assert target.is_relative_to(ROOT.resolve()), f"Link escapes site: {link}"
        if target.is_dir():
            target /= "index.html"
        assert target.is_file(), f"Broken local link: {path}: {link}"
        if url.fragment:
            assert target in pages and url.fragment in pages[target].ids, link

css = (ROOT / "styles.css").read_text(encoding="utf-8")
assert "@import" not in css and "url(" not in css, "Unexpected external CSS resource"
policy = (ROOT / "privacy" / "index.html").read_text(encoding="utf-8")
for required in ("ShardCoders", "DPAPI", "1–30 days", "Clear history", "Support and feedback", "This website"):
    assert required in policy, f"Missing policy information: {required}"
print("Static site checks passed: semantic pages, local links, retention, privacy and no active content.")
