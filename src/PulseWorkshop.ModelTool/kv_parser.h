#pragma once
#include <string>
#include <vector>

// Minimal Source KeyValues v1 node. A leaf has a non-empty value and empty children;
// a block has children and an empty value.
struct KvNode {
    std::string key;
    std::string value;
    std::vector<KvNode> children;
};

// Parse Source KV1 text. Returns a root node (key="") whose children are the top-level entries.
KvNode kv_parse(const std::string& src);

// Serialize a VMT block: '"shader"\n{\n...\n}\n'
std::string kv_serialize(const std::string& shader, const std::vector<KvNode>& children);

// Case-insensitive lookup of a direct child by key. Returns nullptr if not found.
const KvNode* kv_find(const std::vector<KvNode>& nodes, const std::string& key);
KvNode*       kv_find(std::vector<KvNode>& nodes, const std::string& key);
