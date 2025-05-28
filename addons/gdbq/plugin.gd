@tool
extends EditorPlugin

const API_KEY_SETTING = "bugquest/api_key"
const SECRET_KEY_SETTING = "bugquest/secret_key"
const BASE_URL = "https://ingest.bugquestggapi.com"
const EDITOR_INIT_ENDPOINT = "editor/init"
const SETUP_URL = BASE_URL + "/" + EDITOR_INIT_ENDPOINT
const EULA_URL = "https://www.bugquest.gg/legal/eula"
const PRIVACY_URL = "https://www.bugquest.gg/legal/privacy"
const CONSOLE_URL = "https://console.bugquest.gg/register"

var setup_dialog: AcceptDialog
var completion_dialog: AcceptDialog
var http_request: HTTPRequest
var setup_btn: Button = null  # Direct reference to the setup button

func _enter_tree():
    add_tool_menu_item("BugQuest Setup", Callable(self, "_show_plugin_main_dialog"))

func _exit_tree():
    remove_tool_menu_item("BugQuest Setup")
    
    # Clean up dialogs when plugin is unloaded
    if setup_dialog:
        setup_dialog.queue_free()
    if completion_dialog:
        completion_dialog.queue_free()

# Main entry point - checks if API keys exist and shows appropriate dialog
func _show_plugin_main_dialog():
    # Check if API key and secret key already exist in project settings
    var api_key = ProjectSettings.get_setting(API_KEY_SETTING, "")
    var secret_key = ProjectSettings.get_setting(SECRET_KEY_SETTING, "")
    
    if api_key.is_empty() or secret_key.is_empty():
        # No API keys - show the initial setup dialog
        _show_setup_dialog()
    else:
        # API keys exist - show the completion dialog with reset option
        _show_done_dialog(api_key, secret_key)

func _show_setup_dialog():
    if not setup_dialog:
        setup_dialog = AcceptDialog.new()
        setup_dialog.title = "BugQuest Setup"
        setup_dialog.min_size = Vector2(500, 350)  # Increased height for OK button
        setup_dialog.max_size = Vector2(500, 350)  # Fixed size
        # Force dialog not to expand
        setup_dialog.size = Vector2(500, 350)
        # Set the OK button text
        setup_dialog.ok_button_text = "Close"
        
        # Connect close button pressed signal
        setup_dialog.connect("confirmed", Callable(self, "_on_setup_dialog_closed"))
        
        # In Godot 4, add dialogs to the editor main screen
        get_editor_interface().get_base_control().add_child(setup_dialog)
        
        # UI - Use a PanelContainer with padding that won't expand
        var panel = PanelContainer.new()
        panel.size_flags_vertical = Control.SIZE_SHRINK_END
        
        # Use MarginContainer to add padding inside panel
        var margin = MarginContainer.new()
        margin.add_theme_constant_override("left", 20)
        margin.add_theme_constant_override("right", 20)
        margin.add_theme_constant_override("top", 20)
        margin.add_theme_constant_override("bottom", 50)  # Extra bottom padding for OK button
        panel.add_child(margin)
        
        # Add panel to dialog
        setup_dialog.add_child(panel)
        
        # Main container - specifically prevent vertical expansion
        var vb = VBoxContainer.new()
        vb.name = "BQ_MainContainer"
        vb.size_flags_horizontal = Control.SIZE_EXPAND_FILL
        vb.size_flags_vertical = Control.SIZE_SHRINK_CENTER
        margin.add_child(vb)
        
        # Label with welcome text
        var label = Label.new()
        label.text = "Click Setup to register this project for error logging."
        vb.add_child(label)
        
        # Spacer
        var spacer1 = Control.new()
        spacer1.custom_minimum_size.y = 15
        vb.add_child(spacer1)
        
        # EULA checkbox
        var chk = CheckBox.new()
        chk.text = "I agree to the EULA and Privacy Policy"
        vb.add_child(chk)
        chk.connect("toggled", Callable(self, "_on_accept_toggled"))
        
        # Spacer
        var spacer2 = Control.new()
        spacer2.custom_minimum_size.y = 5
        vb.add_child(spacer2)
        
        # Links row
        var link_bar = HBoxContainer.new()
        link_bar.size_flags_horizontal = Control.SIZE_SHRINK_CENTER
        
        # EULA button
        var eula_btn = Button.new()
        eula_btn.text = "EULA"
        eula_btn.connect("pressed", Callable(self, "_open_url").bind(EULA_URL))
        link_bar.add_child(eula_btn)
        
        # Privacy button
        var pp_btn = Button.new()
        pp_btn.text = "Privacy"
        pp_btn.connect("pressed", Callable(self, "_open_url").bind(PRIVACY_URL))
        link_bar.add_child(pp_btn)
        
        vb.add_child(link_bar)
        
        # Spacer
        var spacer3 = Control.new()
        spacer3.custom_minimum_size.y = 25
        vb.add_child(spacer3)
        
        # Setup button - make it larger
        var btn = Button.new()
        btn.name = "bq_setup_btn"
        btn.text = "Setup"
        btn.disabled = true
        btn.size_flags_horizontal = Control.SIZE_SHRINK_CENTER
        btn.custom_minimum_size.x = 200  # Make the button wider
        btn.custom_minimum_size.y = 40   # Make the button taller
        vb.add_child(btn)
        
        # Store direct reference to button
        setup_btn = btn
        btn.connect("pressed", Callable(self, "_on_setup_pressed"))
    
    # Show dialog with explicit size
    setup_dialog.popup_centered(Vector2(500, 350))

func _on_setup_dialog_closed():
    print("Setup dialog closed with OK button")

func _on_accept_toggled(pressed: bool):
    # Debug information
    print("Checkbox toggled: " + str(pressed))
    
    # Use our direct reference to the button
    if setup_btn:
        setup_btn.disabled = not pressed
        print("Setup button enabled: " + str(not setup_btn.disabled))
    else:
        print("ERROR: No direct reference to setup button")

func _open_url(url: String):
    OS.shell_open(url)

func _on_setup_pressed():
    # Use our direct reference
    if setup_btn:
        setup_btn.disabled = true
        print("Setup button disabled")
    else:
        print("ERROR: No direct reference to setup button")
        
    # Create HTTPRequest and add it to the editor interface root control
    # This is important to prevent it from modifying the setup dialog's size
    http_request = HTTPRequest.new()
    get_editor_interface().get_base_control().add_child(http_request)
    http_request.connect("request_completed", Callable(self, "_on_request_completed"))
    
    # Make the HTTP request with error handling
    var headers = PackedStringArray(["Content-Type: application/json"])
    
    # Try to get BugQuestNode
    var bugquest_node = get_node_or_null("/root/BugQuest")
    
    if bugquest_node:
        # Get proper application info and editor init data from C++ code
        var editor_init_data = bugquest_node.get_editor_init_data()
        print("Using editor init data from C++: " + editor_init_data)
        
        # Debug output - print request details
        print("HTTP Request Details:")
        print("- URL: " + SETUP_URL)
        print("- Method: POST")
        print("- Headers: " + str(headers))
        print("- Body: " + editor_init_data)
        
        var err = http_request.request(SETUP_URL, headers, HTTPClient.METHOD_POST, editor_init_data)
        if err != OK:
            push_error("BugQuest setup failed: Failed to start HTTP request, error code %s" % err)
            if setup_btn:
                setup_btn.disabled = false

func _print_node_hierarchy(node: Node, indent: String = ""):
    print(indent + "- " + node.name + " (" + node.get_class() + ")")
    for child in node.get_children():
        _print_node_hierarchy(child, indent + "  ")

func _on_request_completed(result: int, response_code: int, headers: PackedStringArray, body: PackedByteArray):
    http_request.queue_free()
    
    # Debug output for the response
    print("HTTP Response Details:")
    print("- Result: " + str(result) + " (" + ("OK" if result == OK else "ERROR") + ")")
    print("- Response Code: " + str(response_code))
    print("- Headers: " + str(headers))
    
    # Body may contain useful error details
    var body_str = body.get_string_from_utf8()
    print("- Body: " + body_str)
    
    if result != OK or response_code < 200 or response_code >= 300:
        push_error("BugQuest setup failed: HTTP %s - Body: %s" % [response_code, body_str])
        return
    # Body string was already converted above
    # Parse JSON response
    var parsed = JSON.parse_string(body_str)
    if parsed == null:
        push_error("BugQuest setup failed: JSON parse error")
        return
    var api_key = parsed.get("apiKey", "")
    var secret_key = parsed.get("secretKey", "")
    if api_key == "" or secret_key == "":
        push_error("BugQuest setup failed: missing keys in response")
        return
    
    # Store the API key and secret key in the project settings
    ProjectSettings.set_setting(API_KEY_SETTING, api_key)
    ProjectSettings.set_setting(SECRET_KEY_SETTING, secret_key)
    ProjectSettings.save()
    
    # Hide the setup dialog
    if setup_dialog:
        setup_dialog.hide()
    
    # Show the completion dialog
    _show_done_dialog(api_key, secret_key)

func _show_done_dialog(api_key: String, secret_key: String):
    # Create new dialog if it doesn't exist, or reuse the existing one
    if not completion_dialog:
        completion_dialog = AcceptDialog.new()
        completion_dialog.title = "BugQuest Setup Complete"
        completion_dialog.min_size = Vector2(600, 300)  # Increased height for OK button
        completion_dialog.max_size = Vector2(600, 300)  # Fixed maximum size to prevent stretching
        completion_dialog.size = Vector2(600, 300)      # Force initial size
        completion_dialog.ok_button_text = "Close"      # Rename OK button
        
        # Connect close button press signal
        completion_dialog.connect("confirmed", Callable(self, "_on_completion_dialog_confirmed"))
        
        # In Godot 4, add dialogs to the editor main screen
        get_editor_interface().get_base_control().add_child(completion_dialog)
        
        # Use PanelContainer to provide a fixed-size content area
        var panel = PanelContainer.new()
        panel.size_flags_vertical = Control.SIZE_SHRINK_END
        completion_dialog.add_child(panel)
        
        # Use MarginContainer inside panel for padding
        var margin = MarginContainer.new()
        margin.add_theme_constant_override("left", 20)
        margin.add_theme_constant_override("right", 20)
        margin.add_theme_constant_override("top", 20)
        margin.add_theme_constant_override("bottom", 70)  # Extra bottom padding for OK button
        panel.add_child(margin)
        
        # Main content container
        var vb = VBoxContainer.new()
        vb.name = "BQ_CompletionContainer"
        vb.size_flags_horizontal = Control.SIZE_EXPAND_FILL
        vb.size_flags_vertical = Control.SIZE_SHRINK_CENTER
        margin.add_child(vb)
        
        # Heading
        var lbl = Label.new()
        lbl.text = "Setup complete! Click the link below to open your BugQuest console:"
        vb.add_child(lbl)
        
        # Spacer
        var spacer1 = Control.new()
        spacer1.custom_minimum_size.y = 15
        vb.add_child(spacer1)
        
        # URL container with fixed height and styling
        var url_container = PanelContainer.new()
        url_container.name = "URLContainer"
        url_container.size_flags_horizontal = Control.SIZE_EXPAND_FILL
        url_container.custom_minimum_size.y = 40
        
        # Add padding inside URL container
        var url_margin = MarginContainer.new()
        url_margin.add_theme_constant_override("left", 8)
        url_margin.add_theme_constant_override("right", 8)
        url_margin.add_theme_constant_override("top", 4)
        url_margin.add_theme_constant_override("bottom", 4)
        url_container.add_child(url_margin)
        
        # URL text - we'll update the actual URL later
        var link = RichTextLabel.new()
        link.name = "URLLink"
        link.bbcode_enabled = true
        link.meta_underlined = true
        link.size_flags_horizontal = Control.SIZE_EXPAND_FILL
        link.size_flags_vertical = Control.SIZE_EXPAND_FILL
        link.meta_clicked.connect(func(meta): OS.shell_open(str(meta)))
        link.fit_content = true
        url_margin.add_child(link)
        vb.add_child(url_container)
        
        # Spacer
        var spacer2 = Control.new()
        spacer2.custom_minimum_size.y = 20
        vb.add_child(spacer2)
        
        # Button container - center the buttons
        var btn_container = HBoxContainer.new()
        btn_container.size_flags_horizontal = Control.SIZE_EXPAND_FILL
        btn_container.alignment = BoxContainer.ALIGNMENT_CENTER
        btn_container.custom_minimum_size.y = 40
        
        # Add copy button
        var copy_btn = Button.new()
        copy_btn.text = "Copy Link to Clipboard"
        copy_btn.custom_minimum_size.x = 200
        copy_btn.custom_minimum_size.y = 40
        copy_btn.pressed.connect(Callable(self, "_on_copy_button_pressed"))
        btn_container.add_child(copy_btn)
        
        # Add spacer between buttons
        var btn_spacer = Control.new()
        btn_spacer.custom_minimum_size.x = 20
        btn_container.add_child(btn_spacer)
        
        # Add reset button
        var reset_btn = Button.new()
        reset_btn.text = "Reset API Keys"
        reset_btn.custom_minimum_size.x = 200
        reset_btn.custom_minimum_size.y = 40
        reset_btn.pressed.connect(Callable(self, "_on_reset_button_pressed"))
        btn_container.add_child(reset_btn)
        
        vb.add_child(btn_container)
    
    # Update the URL in the RichTextLabel
    var url = "%s/%s/%s" % [CONSOLE_URL, api_key, secret_key]
    var link_node = completion_dialog.find_child("URLLink", true, false)
    if link_node:
        link_node.text = "[url=%s]%s[/url]" % [url, url]
    
    # Show dialog with explicit size
    completion_dialog.popup_centered(Vector2(600, 300))
    
    # Debug - output the dialog hierarchy to help see what might be wrong
    print("Completion dialog hierarchy:")
    _print_node_hierarchy(completion_dialog)

func _on_copy_button_pressed():
    # Get current API key and secret key
    var api_key = ProjectSettings.get_setting(API_KEY_SETTING, "")
    var secret_key = ProjectSettings.get_setting(SECRET_KEY_SETTING, "")
    
    if api_key.is_empty() or secret_key.is_empty():
        OS.alert("No API key or secret key found", "Error")
        return
    
    # Construct the URL and copy it to clipboard
    var url = "%s/%s/%s" % [CONSOLE_URL, api_key, secret_key]
    DisplayServer.clipboard_set(url)
    OS.alert("URL copied to clipboard", "Success")

func _on_reset_button_pressed():
    # Ask for confirmation before resetting
    var confirm = ConfirmationDialog.new()
    confirm.title = "Confirm Reset"
    confirm.dialog_text = "Are you sure you want to reset the API keys? This will remove your current BugQuest configuration."
    confirm.min_size = Vector2(400, 150)
    get_editor_interface().get_base_control().add_child(confirm)
    
    # Connect confirmation signal
    confirm.connect("confirmed", Callable(self, "_perform_api_key_reset"))
    
    # Show dialog
    confirm.popup_centered()

func _perform_api_key_reset():
    # Remove API key and secret key from project settings
    if ProjectSettings.has_setting(API_KEY_SETTING):
        ProjectSettings.set_setting(API_KEY_SETTING, "")
    
    if ProjectSettings.has_setting(SECRET_KEY_SETTING):
        ProjectSettings.set_setting(SECRET_KEY_SETTING, "")
    
    # Save project settings
    ProjectSettings.save()
    
    # Hide completion dialog
    if completion_dialog:
        completion_dialog.hide()
    
    # Show setup dialog
    _show_setup_dialog()
    
    # Show confirmation
    OS.alert("API keys have been reset.", "Reset Complete")

func _on_completion_dialog_confirmed():
    print("Completion dialog closed with OK button")