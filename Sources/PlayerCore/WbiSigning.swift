import Foundation
import CryptoKit

/// B站 public web request signing. No account credentials are stored or requested.
/// Algorithm reference: https://github.com/Chilfish/bilibili-API/blob/master/docs/misc/sign/wbi.md
public enum WbiSigning {
    public static func query(_ parameters: [String: String], imgKey: String, subKey: String, timestamp: Int) throws -> [URLQueryItem] {
        let order = [46,47,18,2,53,8,23,32,15,50,10,31,58,3,45,35,27,43,5,49,33,9,42,19,29,28,14,39,12,38,41,13]
        let raw = Array((imgKey + subKey).utf8)
        guard raw.count == 64, raw.allSatisfy({ (48...57).contains($0) || (97...102).contains($0) }) else { throw PlayerError.message("B 站搜索签名参数无效，请稍后重试") }
        let mix = String(decoding: order.map { raw[$0] }, as: UTF8.self)
        var values = parameters.mapValues { $0.filter { !"!'()*".contains($0) } }; values["wts"] = String(timestamp)
        let query = values.keys.sorted().map { "\(encode($0))=\(encode(values[$0]!))" }.joined(separator: "&")
        values["w_rid"] = Insecure.MD5.hash(data: Data((query + mix).utf8)).map { String(format: "%02x", $0) }.joined()
        return values.keys.sorted().map { .init(name: $0, value: values[$0]) }
    }
    public static func encode(_ value: String) -> String {
        value.addingPercentEncoding(withAllowedCharacters: CharacterSet(charactersIn: "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~")) ?? ""
    }
}
