class_name NumberText
extends RefCounted
## Number formatting helpers for the UI.


## 1005 -> "1,005", 1234567 -> "1,234,567", -4200 -> "-4,200".
static func with_commas(value: int) -> String:
	var digits := str(absi(value))
	var result := ""
	var count := 0
	for i in range(digits.length() - 1, -1, -1):
		result = digits[i] + result
		count += 1
		if count % 3 == 0 and i > 0:
			result = "," + result
	return ("-" if value < 0 else "") + result
