# - addroleplaytime

cmd-addroleplaytime-desc = Добавляет указанное число минут к времени игрока на определённой роли
cmd-addroleplaytime-help = Использование: { $command } <user name> <role> <minutes>
cmd-addroleplaytime-succeed = Игровое время для { $username } / \'{ $role }\' увеличено на { TOSTRING($time, "dddd\\:hh\\:mm") }.
# Aavikko start: offline variant
cmd-addroleplaytime-succeed-offline = Игрок { $username } не в сети — обновлено напрямую в БД. Новое время для \'{ $role }\': { TOSTRING($time, "dddd\\:hh\\:mm") }.
# Aavikko end
cmd-addroleplaytime-arg-user = <user name>
cmd-addroleplaytime-arg-role = <role>
cmd-addroleplaytime-arg-minutes = <minutes>
cmd-addroleplaytime-max-limit = Время не может превышать { $minutes } минут.
cmd-addroleplaytime-error-args = Ожидается ровно три аргумента

# - adddepartmentplaytime

cmd-adddepartmentplaytime-desc = Добавляет указанное количество минут ко всем ролям указанного отдела
cmd-adddepartmentplaytime-help = Использование: { $command } <department> <user name> <minutes>
cmd-adddepartmentplaytime-succeed = Игровое время для { $username } в отделе '{ $department }' увеличено на { $minutes } минут.
# Aavikko start: offline variant
cmd-adddepartmentplaytime-succeed-offline = Игрок { $username } не в сети — обновлено напрямую в БД. В отделе '{ $department }' всем ролям добавлено { $minutes } минут.
# Aavikko end
cmd-adddepartmentplaytime-arg-department = <department>
cmd-adddepartmentplaytime-arg-user = <user name>
cmd-adddepartmentplaytime-arg-minutes = <minutes>
cmd-adddepartmentplaytime-error-args = Ожидается ровно три аргумента: <department> <user name> <minutes>
cmd-adddepartmentplaytime-invalid-department = Недействительное название отдела: '{ $department }'. Допустимые значения: cargo, civilian, command, engineering, medical, security, science, specific.
cmd-adddepartmentplaytime-max-limit = Максимальное допустимое значение минут: { $minutes }

# - addgeneralplaytime

cmd-addgeneralplaytime-desc = Добавляет указанное число минут к общему игровому времени игрока
cmd-addgeneralplaytime-help = Использование: { $command } <user name> <minutes>
cmd-addgeneralplaytime-succeed = Общее игровое время { $username } увеличено на { TOSTRING($time, "dddd\\:hh\\:mm") }.
# Aavikko start: offline variant
cmd-addgeneralplaytime-succeed-offline = Игрок { $username } не в сети — обновлено напрямую в БД. Новое общее время: { TOSTRING($time, "dddd\\:hh\\:mm") }.
# Aavikko end
cmd-addgeneralplaytime-arg-user = <user name>
cmd-addgeneralplaytime-arg-minutes = <minutes>
cmd-addgeneralplaytime-error-args = Ожидается ровно два аргумента

# - unlockeveryfuckingrole

cmd-unlockEveryRole-desc = Добавляет 1000 минут ко всем ролям для указанного игрока.
cmd-unlockEveryRole-help = Использование: { $command } <user name>
cmd-unlockEveryRole-error-args = Ожидается ровно один аргумент: <user name>
cmd-unlockEveryRole-succeed = Игровое время для всех ролей игрока { $username } увеличено на { $minutes } минут.
# Aavikko start: offline variant
cmd-unlockEveryRole-succeed-offline = Игрок { $username } не в сети — обновлено напрямую в БД. Всем ролям добавлено { $minutes } минут.
# Aavikko end
cmd-unlockEveryRole-arg-user = <user name>
