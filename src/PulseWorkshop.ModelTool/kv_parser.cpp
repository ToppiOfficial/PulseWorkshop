#include "kv_parser.h"
#include <algorithm>
#include <cctype>

namespace {

enum class TokType { String, OpenBrace, CloseBrace, Eof };
struct Token { TokType type; std::string value; };

static std::vector<Token> tokenize(const std::string& src) {
    std::vector<Token> tokens;
    size_t i = 0;
    const size_t n = src.size();

    while (i < n) {
        while (i < n && std::isspace(static_cast<unsigned char>(src[i]))) ++i;
        if (i >= n) break;

        // Line comment
        if (i + 1 < n && src[i] == '/' && src[i + 1] == '/') {
            while (i < n && src[i] != '\n') ++i;
            continue;
        }

        if (src[i] == '{') { tokens.push_back({TokType::OpenBrace,  {}}); ++i; continue; }
        if (src[i] == '}') { tokens.push_back({TokType::CloseBrace, {}}); ++i; continue; }

        if (src[i] == '"') {
            ++i;
            std::string val;
            while (i < n && src[i] != '"') {
                if (src[i] == '\\' && i + 1 < n) {
                    char next = src[i + 1];
                    switch (next) {
                        case 'n':  val += '\n'; break;
                        case 't':  val += '\t'; break;
                        // For all other escapes (e.g. backslashes in VMT texture paths like
                        // "models\survivors\survivor_it"), keep both the backslash and the char.
                        default:   val += '\\'; val += next; break;
                    }
                    i += 2;
                } else {
                    val += src[i++];
                }
            }
            if (i < n) ++i; // skip closing "
            tokens.push_back({TokType::String, std::move(val)});
            continue;
        }

        // Unquoted token - stop at whitespace, braces, or quotes
        std::string val;
        while (i < n
               && !std::isspace(static_cast<unsigned char>(src[i]))
               && src[i] != '{' && src[i] != '}' && src[i] != '"') {
            val += src[i++];
        }
        if (!val.empty()) tokens.push_back({TokType::String, std::move(val)});
    }

    tokens.push_back({TokType::Eof, {}});
    return tokens;
}

static std::vector<KvNode> parse_children(const std::vector<Token>& toks, size_t& pos) {
    std::vector<KvNode> out;
    while (pos < toks.size()
           && toks[pos].type != TokType::CloseBrace
           && toks[pos].type != TokType::Eof) {

        if (toks[pos].type != TokType::String) { ++pos; continue; }

        KvNode node;
        node.key = toks[pos++].value;

        if (pos >= toks.size() || toks[pos].type == TokType::Eof) {
            out.push_back(std::move(node));
            break;
        }

        if (toks[pos].type == TokType::OpenBrace) {
            ++pos; // consume {
            node.children = parse_children(toks, pos);
            if (pos < toks.size() && toks[pos].type == TokType::CloseBrace) ++pos;
        } else if (toks[pos].type == TokType::String) {
            node.value = toks[pos++].value;
        }

        out.push_back(std::move(node));
    }
    return out;
}

static void write_block(std::string& out, const std::vector<KvNode>& nodes, int depth) {
    std::string indent(static_cast<size_t>(depth), '\t');
    for (const auto& n : nodes) {
        if (n.children.empty()) {
            // Escape quotes in value for safe round-tripping
            std::string esc;
            esc.reserve(n.value.size());
            for (char c : n.value) {
                if (c == '"') esc += "\\\"";
                else esc += c;
            }
            out += indent + '"' + n.key + "\"\t\"" + esc + "\"\n";
        } else {
            out += indent + '"' + n.key + "\"\n" + indent + "{\n";
            write_block(out, n.children, depth + 1);
            out += indent + "}\n";
        }
    }
}

static std::string key_lower(const std::string& s) {
    std::string r = s;
    std::transform(r.begin(), r.end(), r.begin(),
                   [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
    return r;
}

} // namespace

KvNode kv_parse(const std::string& src) {
    auto toks = tokenize(src);
    size_t pos = 0;
    KvNode root;
    root.children = parse_children(toks, pos);
    return root;
}

std::string kv_serialize(const std::string& shader, const std::vector<KvNode>& children) {
    std::string out;
    out += '"';
    out += shader;
    out += "\"\n{\n";
    write_block(out, children, 1);
    out += "}\n";
    return out;
}

const KvNode* kv_find(const std::vector<KvNode>& nodes, const std::string& key) {
    const std::string kl = key_lower(key);
    for (const auto& n : nodes) {
        if (key_lower(n.key) == kl) return &n;
    }
    return nullptr;
}

KvNode* kv_find(std::vector<KvNode>& nodes, const std::string& key) {
    return const_cast<KvNode*>(kv_find(const_cast<const std::vector<KvNode>&>(nodes), key));
}
