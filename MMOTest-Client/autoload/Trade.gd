extends Node
## Autoload "Trade": mirror of the local player's current trade state.
## Populated by Network signals; consumed by trade_panel.gd and anything else
## that needs to know "am I in a trade right now?"

signal state_changed
signal invite_changed
signal error_received(error_key: String, error_params: Dictionary)
signal cancelled(error_key: String, error_params: Dictionary)
signal completed
signal offer_amount_requested(item_id: int)

var session_id: String = ""
var partner_id: int = 0
var partner_username: String = ""
var phase: String = ""                # "offering" | "confirming"
var my_offer: Array = []              # [{item_id, slug, display, quantity, stackable, cert_of_slug}]
var their_offer: Array = []
var my_credits: int = 0
var their_credits: int = 0
var my_accepted: bool = false
var their_accepted: bool = false
var my_confirmed: bool = false
var their_confirmed: bool = false

var pending_invite: Dictionary = {}   # {invite_id, from_player_id, from_username}
var invite_pending: bool = false


func is_active() -> bool:
	return not session_id.is_empty()


func has_invite() -> bool:
	return not pending_invite.is_empty()


func clear() -> void:
	session_id = ""
	partner_id = 0
	partner_username = ""
	phase = ""
	my_offer = []
	their_offer = []
	my_credits = 0
	their_credits = 0
	my_accepted = false
	their_accepted = false
	my_confirmed = false
	their_confirmed = false
	pending_invite = {}
	invite_pending = false


func offered_quantity(item_id: int) -> int:
	var total := 0
	for entry in my_offer:
		if int(entry.get("item_id", 0)) == item_id:
			total += int(entry.get("quantity", 0))
	return total


func _ready() -> void:
	Network.trade_request_incoming.connect(_on_incoming)
	Network.trade_request_cancelled.connect(_on_invite_cancelled)
	Network.trade_state_received.connect(_on_state)
	Network.trade_error.connect(_on_error)
	Network.trade_cancelled.connect(_on_cancelled)
	Network.trade_completed.connect(_on_completed)
	Network.state_changed.connect(_on_network_state)


func _on_incoming(invite_id: String, from_player_id: int, from_username: String) -> void:
	pending_invite = {
		"invite_id": invite_id,
		"from_player_id": from_player_id,
		"from_username": from_username,
	}
	invite_pending = false
	invite_changed.emit()


func _on_invite_cancelled(invite_id: String, error_key: String, error_params: Dictionary) -> void:
	if str(pending_invite.get("invite_id", "")) != invite_id:
		return
	pending_invite = {}
	invite_pending = false
	invite_changed.emit()
	error_received.emit(error_key, error_params)


func _on_state(state: Dictionary) -> void:
	session_id       = str(state.get("session_id", ""))
	partner_id       = int(state.get("partner_id", 0))
	partner_username = str(state.get("partner_username", ""))
	phase            = str(state.get("phase", "offering"))
	my_offer         = state.get("my_offer", [])
	their_offer      = state.get("their_offer", [])
	my_credits       = int(state.get("my_credits", 0))
	their_credits    = int(state.get("their_credits", 0))
	my_accepted      = bool(state.get("my_accepted", false))
	their_accepted   = bool(state.get("their_accepted", false))
	my_confirmed     = bool(state.get("my_confirmed", false))
	their_confirmed  = bool(state.get("their_confirmed", false))
	pending_invite = {}
	state_changed.emit()


func _on_error(error_key: String, error_params: Dictionary) -> void:
	error_received.emit(error_key, error_params)


func _on_cancelled(_sid: String, error_key: String, error_params: Dictionary) -> void:
	clear()
	state_changed.emit()
	cancelled.emit(error_key, error_params)


func _on_completed(_sid: String) -> void:
	clear()
	state_changed.emit()
	completed.emit()


func _on_network_state(state: int) -> void:
	if state == Network.State.DISCONNECTED and (is_active() or has_invite()):
		clear()
		state_changed.emit()
		invite_changed.emit()
