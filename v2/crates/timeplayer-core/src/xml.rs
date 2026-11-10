//! A small XML tree that matches names without their namespace prefix (TubeRepair's feeds use different
//! namespaces depending on the server; YouTube's RSS feeds use yt: and media:).

use quick_xml::events::Event;

#[derive(Debug, Clone, Default)]
pub struct Element {
    /// The local name, without a prefix.
    pub name: String,
    pub attrs: Vec<(String, String)>,
    pub children: Vec<Element>,
    pub text: String,
}

fn local(name: &[u8]) -> String {
    let full = String::from_utf8_lossy(name);
    full.rsplit(':').next().unwrap_or(&full).to_owned()
}

impl Element {
    pub fn child(&self, name: &str) -> Option<&Element> {
        self.children.iter().find(|c| c.name.eq_ignore_ascii_case(name))
    }

    pub fn children_named<'a>(&'a self, name: &'a str) -> impl Iterator<Item = &'a Element> + 'a {
        self.children.iter().filter(move |c| c.name.eq_ignore_ascii_case(name))
    }

    pub fn attr(&self, name: &str) -> Option<&str> {
        self.attrs.iter().find(|(k, _)| k.eq_ignore_ascii_case(name)).map(|(_, v)| v.as_str())
    }

    /// The trimmed text of a child, if it has any.
    pub fn child_text(&self, name: &str) -> Option<String> {
        self.child(name).map(|c| c.text.trim().to_owned()).filter(|t| !t.is_empty())
    }

    /// Every element named `name` at any depth.
    pub fn descendants<'a>(&'a self, name: &str, out: &mut Vec<&'a Element>) {
        for c in &self.children {
            if c.name.eq_ignore_ascii_case(name) {
                out.push(c);
            }
            c.descendants(name, out);
        }
    }
}

/// Parses a document; None when it isn't well-formed.
pub fn parse(text: &str) -> Option<Element> {
    let mut reader = quick_xml::Reader::from_str(text);
    reader.config_mut().check_end_names = false;
    let mut stack: Vec<Element> = vec![Element::default()];
    loop {
        match reader.read_event() {
            Ok(Event::Start(e)) => {
                stack.push(open(&e));
            }
            Ok(Event::Empty(e)) => {
                let el = open(&e);
                stack.last_mut()?.children.push(el);
            }
            Ok(Event::End(_))
                if stack.len() > 1 => {
                    let done = stack.pop()?;
                    stack.last_mut()?.children.push(done);
                }
            Ok(Event::Text(t)) => {
                let s = t.unescape().map(|s| s.into_owned()).unwrap_or_else(|_| String::from_utf8_lossy(&t).into_owned());
                stack.last_mut()?.text.push_str(&s);
            }
            Ok(Event::CData(t)) => stack.last_mut()?.text.push_str(&String::from_utf8_lossy(&t)),
            Ok(Event::Eof) => break,
            Err(_) => return None,
            _ => {}
        }
    }
    while stack.len() > 1 {
        let done = stack.pop()?;
        stack.last_mut()?.children.push(done);
    }
    let mut root = stack.pop()?;
    if root.children.len() == 1 { root.children.pop() } else { Some(root) }
}

fn open(e: &quick_xml::events::BytesStart) -> Element {
    Element {
        name: local(e.name().as_ref()),
        attrs: e
            .attributes()
            .flatten()
            .map(|a| (local(a.key.as_ref()), a.unescape_value().map(|v| v.into_owned()).unwrap_or_default()))
            .collect(),
        ..Default::default()
    }
}
