<?php
session_start(); 
header('Content-Type: application/json');
$raw  = file_get_contents('php://input');
$data = json_decode($raw, true);
if (!$data || !isset($data['url']) || !isset($data['result'])) {
  http_response_code(400);
  echo json_encode(['status'=>'error','message'=>'Invalid data']);
  exit;
}

$url    = trim((string)$data['url']);
$allOk  = !empty($data['allOk']);
$checks = is_array($data['result']) ? $data['result'] : [];



function extractEmail($v) {
    if (!is_string($v)) return null;
    $v = trim($v);
    if (preg_match('/<([^>]+)>/', $v, $m)) {
        $v = $m[1];
    }
    $v = preg_replace('/^mailto:/i', '', $v);
    $v = filter_var($v, FILTER_SANITIZE_EMAIL);
    return filter_var($v, FILTER_VALIDATE_EMAIL) ?: null;
    error_log('auth_user=' . var_export($_SESSION['auth_user'] ?? null, true));
}

$rawAuth = $_SESSION['auth_user'] ?? '';
$authEmail = extractEmail($rawAuth);
$to = $authEmail ?: 'theadvertninja@gmail.com'; 
$from    = 'cemex@advert.ninja';           
$subject = "Verdikt ke stránce: ".$url;

$html  = "<div style='background:#fff !important; border-radius:20px;padding:20px;font-family:Arial,sans-serif;color:#111;'>";
$html .= "<div style='background:#fff !important; text-align:center; margin-bottom:20px;'>
            <img src='https://cemex.advert.ninja/tools/MigrationBob/migrationbob-300x300.webp' alt='MigrationBob' style='width:120px;height:auto;border-radius:12px;'/>
          </div>";
$html .= "<h2 style='margin-top:0;'>Bobův verdikt</h2>";
$html .= "<p><strong>URL:</strong> ".htmlspecialchars($url, ENT_QUOTES, 'UTF-8')."</p>";
$html .= "<p><strong>Celkový výsledek:</strong> ".($allOk ? "Fantazie" : "Nic moc")."</p><ul>";
foreach ($checks as $c) {
  $ok = !empty($c['Ok']);
  $lab = htmlspecialchars($c['Check'] ?? '', ENT_QUOTES, 'UTF-8');
  $det = htmlspecialchars($c['Details'] ?? '', ENT_QUOTES, 'UTF-8');
  $html .= "<li>".($ok ? "Dobrý" : "Nedobrý")." {$lab}".($det ? " – {$det}" : "")."</li>";
}
$html .= "</ul><p style='color:#777;font-size:12px'>Odesláno: ".date('Y-m-d H:i:s')."</p>";
$html .= "</div>";
$headers  = "MIME-Version: 1.0\r\n";
$headers .= "Content-Type: text/html; charset=UTF-8\r\n";
$headers .= "From: MigrationBob <{$from}>\r\n";
$headers .= "Reply-To: {$from}\r\n";
$ok = @mail($to, $subject, $html, $headers, "-f{$from}");
echo json_encode($ok ? ['status'=>'success'] : ['status'=>'error','message'=>'mail() failed']);
