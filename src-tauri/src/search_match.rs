//! 普通搜索共用查询：先匹配原文，必要时才生成拼音；非汉字原样保留，避免跨字段误命中。
use pinyin::ToPinyin;

pub struct SearchQuery {
    terms: Vec<String>,
}

impl SearchQuery {
    pub fn new(query: &str, exact: bool) -> Self {
        let query = query.trim().to_lowercase();
        let terms = if query.is_empty() {
            vec![]
        } else if exact {
            vec![query]
        } else {
            query.split_whitespace().map(str::to_owned).collect()
        };
        Self { terms }
    }

    pub fn matches(&self, text: &str) -> bool {
        if self.terms.is_empty() {
            return false;
        }
        let original = text.to_lowercase();
        let missing: Vec<_> = self
            .terms
            .iter()
            .filter(|term| !original.contains(term.as_str()))
            .collect();
        if missing.is_empty() {
            return true;
        }
        let mut full = String::with_capacity(original.len());
        let mut initials = String::with_capacity(original.len());
        for ch in original.chars() {
            if let Some(py) = ch.to_pinyin() {
                full.push_str(py.plain());
                initials.push(py.plain().chars().next().unwrap());
            } else {
                full.push(ch);
                initials.push(ch);
            }
        }
        missing
            .iter()
            .all(|term| full.contains(term.as_str()) || initials.contains(term.as_str()))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn original_pinyin_initials_mixed_and_separators() {
        for query in [
            "技术手册",
            "JISHUSHOUCE",
            "JSSC",
            "UPS技术2026",
            "UPSjishu2026",
            "UPSjs2026",
        ] {
            let text = "技术手册 UPS技术2026.pdf";
            assert!(SearchQuery::new(query, false).matches(text), "{query}");
        }
        assert!(!SearchQuery::new("jishushouce", false).matches("技术 手册"));
        assert!(!SearchQuery::new("js", false).matches("技-术"));
        assert!(!SearchQuery::new("js", false).matches("技UPS术"));
        assert!(!SearchQuery::new("", false).matches("技术"));
    }

    #[test]
    fn fuzzy_requires_all_terms_and_exact_keeps_whitespace() {
        assert!(SearchQuery::new("UPS shouce js", false).matches("UPS技术 手册"));
        assert!(!SearchQuery::new("UPS 丢失", false).matches("UPS技术 手册"));
        assert!(!SearchQuery::new("UPS shouce", true).matches("UPS技术 手册"));
        assert!(SearchQuery::new("jishu shouce", true).matches("UPS技术 手册.pdf"));
    }

    #[test]
    fn search_performance_samples() {
        for count in [1_000, 10_000] {
            let records: Vec<_> = (0..count)
                .map(|i| format!("技术手册{i} UPS设备说明2026.pdf 售后服务 联系客服"))
                .collect();
            for input in [
                "技术",
                "jishushouce",
                "jssc",
                "UPS sb",
                "l",
                "li",
                "lian",
                "lianxi",
            ] {
                let query = SearchQuery::new(input, false);
                let start = std::time::Instant::now();
                let matches = records.iter().filter(|text| query.matches(text)).count();
                assert_eq!(matches, count);
                println!(
                    "search records={count} query={input} elapsed_us={}",
                    start.elapsed().as_micros()
                );
            }
        }
    }
}
